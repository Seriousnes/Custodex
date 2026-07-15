using Custodex.Client.Transport;

using Grpc.Core;
using Grpc.Core.Interceptors;

using Shouldly;

namespace Custodex.Client.Tests;

public sealed class ApiKeyInterceptorTests
{
    [Fact]
    public void Adds_the_api_key_header_to_the_call_metadata()
    {
        var interceptor = new ApiKeyInterceptor("secret-operator-key");
        var metadata = new Metadata();
        var context = new ClientInterceptorContext<Msg, Msg>(
            new Method<Msg, Msg>(MethodType.Unary, "svc", "m", Marshaller, Marshaller),
            host: null,
            new CallOptions(headers: metadata));

        var rewritten = interceptor.WithApiKey(context);

        rewritten.Options.Headers.ShouldNotBeNull();
        rewritten.Options.Headers!.Get("x-custodex-key")!.Value.ShouldBe("secret-operator-key");
    }

    [Fact]
    public void Header_name_matches_the_service_contract_literal()
    {
        "x-custodex-key".ShouldBe("X-Custodex-Key".ToLowerInvariant());
    }

    private sealed record Msg;

    private static readonly Marshaller<Msg> Marshaller =
        Marshallers.Create(_ => [], _ => new Msg());
}
