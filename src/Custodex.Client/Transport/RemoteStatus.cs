using Custodex.Abstractions;

using Grpc.Core;

namespace Custodex.Client.Transport;

/// <summary>
/// Catches <see cref="RpcException"/>s from gRPC calls and re-throws them as the
/// corresponding <see cref="Custodex.Abstractions"/> typed exception when a
/// <c>Custodex-error-kind</c> trailer is present; unrecognized errors pass through.
/// </summary>
public static class RemoteStatus
{
    /// <summary>Executes <paramref name="call"/>, translating Custodex gRPC errors to typed exceptions.</summary>
    public static T Unwrap<T>(Func<T> call)
    {
        try { return call(); }
        catch (RpcException ex) { throw Translate(ex); }
    }

    /// <summary>Awaits <paramref name="call"/>, translating Custodex gRPC errors to typed exceptions.</summary>
    public static async Task<T> UnwrapAsync<T>(Func<Task<T>> call)
    {
        try { return await call().ConfigureAwait(false); }
        catch (RpcException ex) { throw Translate(ex); }
    }

    private static Exception Translate(RpcException ex)
    {
        var kind = ex.Trailers.GetValue("custodex-error-kind");
        return kind switch
        {
            "unknown_type" =>
                new UnknownTypeException(ex.Trailers.GetValue("custodex-error-type") ?? string.Empty),
            "unknown_relation" =>
                new UnknownRelationException(
                    ex.Trailers.GetValue("custodex-error-type") ?? string.Empty,
                    ex.Trailers.GetValue("custodex-error-name") ?? string.Empty),
            "unknown_permission" =>
                new UnknownPermissionException(
                    ex.Trailers.GetValue("custodex-error-type") ?? string.Empty,
                    ex.Trailers.GetValue("custodex-error-name") ?? string.Empty),
            "schema_invalid" =>
                new SchemaValidationException(
                    ex.Trailers
                        .Where(e => e.Key == "custodex-error-message")
                        .Select(e => e.Value)
                        .ToList()),
            "evaluation_limit" =>
                new EvaluationLimitException(ex.Trailers.GetValue("custodex-error-detail") ?? ex.Message),
            _ => ex,
        };
    }
}
