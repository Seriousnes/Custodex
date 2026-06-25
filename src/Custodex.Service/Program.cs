using Custodex.Core;
using Custodex.Service.Rest;
using Custodex.Service.Services;
using Custodex.Storage.Postgres;
using Microsoft.OpenApi;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddGrpc(o => o.Interceptors.Add<CustodexExceptionInterceptor>());
builder.Services.AddSingleton<CustodexExceptionInterceptor>();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo { Title = "Custodex Authorization API", Version = "v1" });
});

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

app.UseSwagger();
app.UseSwaggerUI();
app.UseCustodexProblemDetails();

app.MapDefaultEndpoints();
app.MapGrpcService<DecisionGrpcService>();
app.MapGrpcService<RelationsGrpcService>();
app.MapGrpcService<SchemaGrpcService>();
app.MapGrpcService<ProvisioningGrpcService>();
app.MapCustodexRest();

app.Run();

public partial class Program { }
