using Custodex.Abstractions;
using Custodex.Service.Tenancy;

using Grpc.Core;
using Grpc.Core.Interceptors;

namespace Custodex.Service.Services;

/// <summary>
/// Server-side interceptor that converts Custodex typed exceptions into
/// <see cref="RpcException"/>s with <c>custodex-error-kind</c> trailers,
/// preserving the error kind so clients can reconstruct the original exception type.
/// </summary>
public sealed class CustodexExceptionInterceptor : Interceptor
{
    /// <inheritdoc />
    public override async Task<TResponse> UnaryServerHandler<TRequest, TResponse>(
        TRequest request,
        ServerCallContext context,
        UnaryServerMethod<TRequest, TResponse> continuation)
    {
        try
        {
            return await continuation(request, context);
        }
        catch (MissingTenantContextException ex)
        {
            var trailers = new Metadata { { "custodex-error-kind", "missing_tenant" } };
            throw new RpcException(new Status(StatusCode.InvalidArgument, ex.Message), trailers);
        }
        catch (UnknownTypeException ex)
        {
            var trailers = new Metadata
            {
                { "custodex-error-kind", "unknown_type" },
                { "custodex-error-type", ex.Type },
            };
            throw new RpcException(new Status(StatusCode.InvalidArgument, ex.Message), trailers);
        }
        catch (UnknownRelationException ex)
        {
            var trailers = new Metadata
            {
                { "custodex-error-kind", "unknown_relation" },
                { "custodex-error-type", ex.Type },
                { "custodex-error-name", ex.Relation },
            };
            throw new RpcException(new Status(StatusCode.InvalidArgument, ex.Message), trailers);
        }
        catch (UnknownPermissionException ex)
        {
            var trailers = new Metadata
            {
                { "custodex-error-kind", "unknown_permission" },
                { "custodex-error-type", ex.Type },
                { "custodex-error-name", ex.Permission },
            };
            throw new RpcException(new Status(StatusCode.InvalidArgument, ex.Message), trailers);
        }
        catch (SchemaValidationException ex)
        {
            var trailers = new Metadata { { "custodex-error-kind", "schema_invalid" } };
            foreach (var error in ex.Errors)
                trailers.Add("custodex-error-message", error);
            throw new RpcException(new Status(StatusCode.InvalidArgument, ex.Message), trailers);
        }
        catch (EvaluationLimitException ex)
        {
            var trailers = new Metadata
            {
                { "custodex-error-kind", "evaluation_limit" },
                { "custodex-error-detail", ex.Message },
            };
            throw new RpcException(new Status(StatusCode.ResourceExhausted, ex.Message), trailers);
        }
        catch (ExclusionCycleException ex)
        {
            var trailers = new Metadata
            {
                { "custodex-error-kind", "exclusion_cycle" },
                { "custodex-error-detail", ex.Message },
            };
            throw new RpcException(new Status(StatusCode.FailedPrecondition, ex.Message), trailers);
        }
        catch (System.Text.Json.JsonException ex)
        {
            var trailers = new Metadata { { "custodex-error-kind", "malformed_json" } };
            throw new RpcException(new Status(StatusCode.InvalidArgument, ex.Message), trailers);
        }
    }
}
