var builder = DistributedApplication.CreateBuilder(args);

builder.AddProject<Projects.Relkit_Service>("relkit-service");

builder.AddProject<Projects.Relkit_Client>("relkit-client");

builder.Build().Run();
