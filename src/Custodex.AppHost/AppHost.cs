var builder = DistributedApplication.CreateBuilder(args);

builder.AddProject<Projects.Custodex_Service>("Custodex-service");

builder.AddProject<Projects.Custodex_Client>("Custodex-client");

builder.Build().Run();
