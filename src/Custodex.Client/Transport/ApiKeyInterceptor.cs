using Grpc.Core;
using Grpc.Core.Interceptors;

namespace Custodex.Client.Transport;

internal sealed class ApiKeyInterceptor(string apiKey) : Interceptor
{
    private const string ApiKeyHeader = "x-custodex-key";

    internal ClientInterceptorContext<TRequest, TResponse> WithApiKey<TRequest, TResponse>(
        ClientInterceptorContext<TRequest, TResponse> context)
        where TRequest : class
        where TResponse : class
    {
        var headers = context.Options.Headers ?? [];
        if (headers.Get(ApiKeyHeader) is null)
            headers.Add(ApiKeyHeader, apiKey);
        return new ClientInterceptorContext<TRequest, TResponse>(
            context.Method, context.Host, context.Options.WithHeaders(headers));
    }

    public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(
        TRequest request,
        ClientInterceptorContext<TRequest, TResponse> context,
        AsyncUnaryCallContinuation<TRequest, TResponse> continuation) =>
        continuation(request, WithApiKey(context));

    public override TResponse BlockingUnaryCall<TRequest, TResponse>(
        TRequest request,
        ClientInterceptorContext<TRequest, TResponse> context,
        BlockingUnaryCallContinuation<TRequest, TResponse> continuation) =>
        continuation(request, WithApiKey(context));
}
