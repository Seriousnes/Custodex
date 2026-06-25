using Custodex.Client.Transport;
using Custodex.Abstractions;
using Grpc.Core;
using Shouldly;

namespace Custodex.Client.Tests;

public sealed class RemoteStatusTests
{
    [Fact]
    public async Task Unknown_type_trailer_throws_UnknownTypeException()
    {
        var meta = new Metadata
        {
            { "custodex-error-kind", "unknown_type" },
            { "custodex-error-type", "widget" },
        };
        var rpc = new RpcException(new Status(StatusCode.InvalidArgument, "msg"), meta);

        var ex = await Should.ThrowAsync<UnknownTypeException>(() =>
            RemoteStatus.UnwrapAsync(() => Task.FromException<bool>(rpc)));
        ex.Message.ShouldContain("widget");
    }

    [Fact]
    public async Task Unknown_permission_trailers_throw_UnknownPermissionException()
    {
        var meta = new Metadata
        {
            { "custodex-error-kind", "unknown_permission" },
            { "custodex-error-type", "doc" },
            { "custodex-error-name", "edit" },
        };
        var rpc = new RpcException(new Status(StatusCode.InvalidArgument, "msg"), meta);

        var ex = await Should.ThrowAsync<UnknownPermissionException>(() =>
            RemoteStatus.UnwrapAsync(() => Task.FromException<bool>(rpc)));
        ex.Message.ShouldContain("doc");
        ex.Message.ShouldContain("edit");
    }

    [Fact]
    public async Task Schema_invalid_trailers_throw_SchemaValidationException_with_all_errors()
    {
        var meta = new Metadata
        {
            { "custodex-error-kind", "schema_invalid" },
            { "custodex-error-message", "error one" },
            { "custodex-error-message", "error two" },
        };
        var rpc = new RpcException(new Status(StatusCode.FailedPrecondition, "msg"), meta);

        var ex = await Should.ThrowAsync<SchemaValidationException>(() =>
            RemoteStatus.UnwrapAsync(() => Task.FromException<bool>(rpc)));
        ex.Errors.ShouldContain("error one");
        ex.Errors.ShouldContain("error two");
    }

    [Fact]
    public async Task Unavailable_with_no_custodex_trailer_rethrows_RpcException()
    {
        var rpc = new RpcException(new Status(StatusCode.Unavailable, "service down"));

        await Should.ThrowAsync<RpcException>(() =>
            RemoteStatus.UnwrapAsync(() => Task.FromException<bool>(rpc)));
    }
}
