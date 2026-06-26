using Grpc.Net.Client;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

using Testcontainers.PostgreSql;

namespace Custodex.Client.Tests;

[CollectionDefinition("client-parity")]
public sealed class ClientParityCollection : ICollectionFixture<ServiceFixture> { }

public sealed class ServiceFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:16-alpine")
        .Build();

    public WebApplicationFactory<Program> Factory { get; private set; } = null!;
    public GrpcChannel GrpcChannel { get; private set; } = null!;
    public string ConnectionString => _postgres.GetConnectionString();

    internal const string AdminKey = "fixture-admin-key";
    internal const string AdminStore = "fixture-store";

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        Factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(b =>
            {
                b.UseSetting("Custodex:ConnectionString", ConnectionString);
                b.UseEnvironment("Development");
                b.UseSetting("Custodex:ApiKeys:0:Key", AdminKey);
                b.UseSetting("Custodex:ApiKeys:0:Store", "fixture-store");
                b.UseSetting("Custodex:ApiKeys:0:Role", "admin");
            });
        var handler = new ApiKeyHeaderHandler(AdminKey, Factory.Server.CreateHandler());
        GrpcChannel = GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions
        {
            HttpHandler = handler,
        });
    }

    public async Task DisposeAsync()
    {
        GrpcChannel.Dispose();
        await Factory.DisposeAsync();
        await _postgres.DisposeAsync();
    }
}
