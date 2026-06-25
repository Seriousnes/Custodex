using Grpc.Net.Client;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Custodex.Service.Tests;

internal static class TestAuthHelper
{
    internal const string AdminKey = "test-admin-key";
    internal const string AdminStore = "test-admin-store";

    internal static IWebHostBuilder UseAdminApiKey(this IWebHostBuilder b) =>
        b.UseSetting("Custodex:ApiKeys:0:Key", AdminKey)
         .UseSetting("Custodex:ApiKeys:0:Store", AdminStore)
         .UseSetting("Custodex:ApiKeys:0:Role", "admin");

    internal static HttpClient CreateAuthenticatedClient(this WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Custodex-Key", AdminKey);
        return client;
    }

    internal static GrpcChannel CreateAuthenticatedGrpcChannel(this WebApplicationFactory<Program> factory)
    {
        var handler = new ApiKeyHeaderHandler(AdminKey, factory.Server.CreateHandler());
        return GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions { HttpHandler = handler });
    }
}

internal sealed class ApiKeyHeaderHandler(string key, HttpMessageHandler inner) : DelegatingHandler(inner)
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        request.Headers.TryAddWithoutValidation("X-Custodex-Key", key);
        return base.SendAsync(request, ct);
    }
}
