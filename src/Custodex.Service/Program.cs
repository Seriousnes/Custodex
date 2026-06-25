using Custodex.Core;
using Custodex.Storage.Postgres;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddGrpc();

var connectionString = builder.Configuration["Custodex:ConnectionString"]
    ?? throw new InvalidOperationException("Custodex:ConnectionString is required.");

builder.Services.AddCustodex().UsePostgres(connectionString);

var app = builder.Build();

app.MapDefaultEndpoints();

app.Run();

public partial class Program { }
