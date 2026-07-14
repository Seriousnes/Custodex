using Custodex.Abstractions;
using Custodex.AspNetCore;
using Custodex.Core;
using Custodex.Service.Health;
using Custodex.Service.Metrics;
using Custodex.Service.Views;
using Custodex.Storage.Postgres;
using Custodex.Studio;
using Custodex.Studio.Views;

using Microsoft.Extensions.DependencyInjection.Extensions;

using Npgsql;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddOpenTelemetry()
    .WithTracing(t => t.AddCustodexInstrumentation())
    .WithMetrics(m => m.AddCustodexInstrumentation());

builder.Services.AddCustodexService(builder.Configuration);
builder.Services.AddOpenApi(o => o.AddCustodexApiDocumentation());

var connectionString = builder.Configuration.GetConnectionString("Custodex")
    ?? builder.Configuration["Custodex:ConnectionString"]
    ?? throw new InvalidOperationException("Custodex:ConnectionString is required.");

builder.Services.AddCustodex().UsePostgres(connectionString);
builder.Services.AddCustodexStudio();
builder.Services.AddSingleton<IStudioViewStore>(_ => new PostgresStudioViewStore(connectionString));

builder.Services.TryAddSingleton(TimeProvider.System);
builder.Services.AddSingleton<CustodexMeterAggregator>();
builder.Services.AddSingleton<IMetricsSnapshotProvider>(sp => sp.GetRequiredService<CustodexMeterAggregator>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<CustodexMeterAggregator>());
builder.Services.AddHealthChecks()
    .AddCheck<PostgresReadyHealthCheck>("postgres", tags: ["ready"]);

var app = builder.Build();

if (app.Configuration.GetValue("Custodex:ApplyMigrationsOnStartup", true))
{
    await using var conn = new NpgsqlConnection(connectionString);
    await conn.OpenAsync();
    await MigrationRunner.ApplyAsync(conn);
}

app.UseCustodexProblemDetails();

app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();
app.UseCustodexTenantResolution();

app.MapStaticAssets();
app.MapDefaultEndpoints();
app.MapOpenApi();
app.MapCustodex();
app.MapCustodexStudio();

app.Run();
