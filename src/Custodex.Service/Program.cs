using Custodex.Core;
using Custodex.Service.Auth;
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

builder.Services
    .AddAuthentication("ApiKey")
    .AddScheme<ApiKeyOptions, ApiKeyAuthenticationHandler>("ApiKey", _ => { });

builder.Services.AddOptions<ApiKeyOptions>("ApiKey")
    .Configure<IConfiguration>((opts, config) =>
        config.GetSection("Custodex:ApiKeys").Bind(opts.Keys));

builder.Services.AddAuthorization();

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

app.UseAuthentication();
app.UseAuthorization();

app.MapDefaultEndpoints();
app.MapGrpcService<DecisionGrpcService>().RequireAuthorization();
app.MapGrpcService<RelationsGrpcService>().RequireAuthorization();
app.MapGrpcService<SchemaGrpcService>().RequireAuthorization();
app.MapGrpcService<ProvisioningGrpcService>().RequireAuthorization();
app.MapCustodexRest();

app.Run();

public partial class Program { }
