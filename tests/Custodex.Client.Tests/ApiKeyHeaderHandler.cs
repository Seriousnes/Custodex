namespace Custodex.Client.Tests;

internal sealed class ApiKeyHeaderHandler(string key, HttpMessageHandler inner) : DelegatingHandler(inner)
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        request.Headers.TryAddWithoutValidation("X-Custodex-Key", key);
        return base.SendAsync(request, ct);
    }
}
