var builder = DistributedApplication.CreateBuilder(args);

var postgres = builder.AddPostgres("postgres")
    .WithDataVolume("custodex-postgres-data")
    .AddDatabase("Custodex");

builder.AddProject<Projects.Custodex_Service>("custodex-service")
    .WithReference(postgres)
    .WaitFor(postgres);

builder.Build().Run();
