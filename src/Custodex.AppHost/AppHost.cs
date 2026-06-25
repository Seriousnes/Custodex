var builder = DistributedApplication.CreateBuilder(args);

builder.AddProject<Projects.Custodex_Service>("Custodex-service");

builder.Build().Run();
