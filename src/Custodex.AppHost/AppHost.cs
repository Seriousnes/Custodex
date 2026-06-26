var builder = DistributedApplication.CreateBuilder(args);

var postgres = builder.AddPostgres("postgres")
    .WithImageTag("18")    
    .WithDataVolume("custodex-postgres-data")
    .WithPgAdmin(c => c.WithImageTag("latest"))
    .AddDatabase("Custodex");

var service = builder.AddProject<Projects.Custodex_Service>("custodex-service")
    .WithReference(postgres)
    .WaitFor(postgres)
    .WithEnvironment("ASPNETCORE_ENVIRONMENT", "Development")
    .WithUrlForEndpoint("https", url =>
    {
        url.Url = "/swagger/index.html";
        url.DisplayText = "Swagger UI";
    });

var testKey = builder.Configuration["Custodex:TestAdminKey"];
if (!string.IsNullOrEmpty(testKey))
{
    var testStore = builder.Configuration["Custodex:TestAdminStore"] ?? "test-store";
    service
        .WithEnvironment("Custodex__ApiKeys__0__Key", testKey)
        .WithEnvironment("Custodex__ApiKeys__0__Store", testStore)
        .WithEnvironment("Custodex__ApiKeys__0__Role", "admin");
}

builder.Build().Run();
