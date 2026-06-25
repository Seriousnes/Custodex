using Custodex.Core;
using Custodex.Service.Services;
using Custodex.Storage.Postgres;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddGrpc();

var connectionString = builder.Configuration["Custodex:ConnectionString"]
    ?? throw new InvalidOperationException("Custodex:ConnectionString is required.");

builder.Services.AddCustodex().UsePostgres(connectionString);

var app = builder.Build();

if (app.Configuration.GetValue("Custodex:ApplyMigrationsOnStartup", true))
{
    await using var conn = new NpgsqlConnection(connectionString);
    await conn.OpenAsync();
    await MigrationRunner.ApplyAsync(conn);
}

app.MapDefaultEndpoints();
app.MapGrpcService<DecisionGrpcService>();
app.MapGrpcService<RelationsGrpcService>();
app.MapGrpcService<SchemaGrpcService>();
app.MapGrpcService<ProvisioningGrpcService>();

app.Run();

public partial class Program { }
