using System.Security.Claims;

using Custodex.Core;
using Custodex.Service;
using Custodex.Service.Auth;
using Custodex.Service.Health;
using Custodex.Service.Metrics;
using Custodex.Service.OpenApi;
using Custodex.Service.Rest;
using Custodex.Service.Services;
using Custodex.Service.Tenancy;
using Custodex.Service.Views;
using Custodex.Storage.Postgres;
using Custodex.Studio;
using Custodex.Studio.Metrics;
using Custodex.Studio.Views;

using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Tokens;

using Npgsql;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddOpenTelemetry()
    .WithTracing(t => t.AddCustodexInstrumentation())
    .WithMetrics(m => m.AddCustodexInstrumentation());
builder.Services.AddGrpc(o => o.Interceptors.Add<CustodexExceptionInterceptor>());
builder.Services.AddSingleton<CustodexExceptionInterceptor>();
builder.Services.AddOpenApi(o =>
{
    o.AddDocumentTransformer<CustodexOpenApiDocumentTransformer>();
    o.AddOperationTransformer<CustodexOpenApiOperationTransformer>();
});

var jwtSection = builder.Configuration.GetSection("Custodex:Jwt");
var signingKeyB64 = jwtSection["SigningKey"];

builder.Services
    .AddAuthentication("Custodex-any")
    .AddPolicyScheme("Custodex-any", "ApiKey or Bearer", o =>
    {
        o.ForwardDefaultSelector = ctx =>
            ctx.Request.Headers.ContainsKey("X-Custodex-Key") ? "ApiKey" : JwtBearerDefaults.AuthenticationScheme;
        o.ForwardChallenge = JwtBearerDefaults.AuthenticationScheme;
    })
    .AddScheme<ApiKeyOptions, ApiKeyAuthenticationHandler>("ApiKey", _ => { })
    .AddJwtBearer(jwt =>
    {
        var storeClaim = jwtSection["StoreClaim"] ?? "Custodex:store";
        var roleClaim = jwtSection["RoleClaim"] ?? "Custodex:role";

        if (!string.IsNullOrEmpty(signingKeyB64))
        {
            var key = new SymmetricSecurityKey(Convert.FromBase64String(signingKeyB64));
            var issuer = jwtSection["Issuer"];
            var audience = jwtSection["Audience"];
            jwt.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = key,
                ValidateIssuer = !string.IsNullOrEmpty(issuer),
                ValidIssuer = issuer,
                ValidateAudience = !string.IsNullOrEmpty(audience),
                ValidAudience = audience,
                NameClaimType = ClaimTypes.NameIdentifier,
                RoleClaimType = roleClaim,
            };
        }
        else if (!string.IsNullOrEmpty(jwtSection["Authority"]))
        {
            jwt.Authority = jwtSection["Authority"];
            jwt.Audience = jwtSection["Audience"];
            jwt.TokenValidationParameters = new TokenValidationParameters
            {
                NameClaimType = ClaimTypes.NameIdentifier,
                RoleClaimType = roleClaim,
            };
        }
    });

builder.Services.AddOptions<ApiKeyOptions>("ApiKey")
    .Configure<IConfiguration>((opts, config) =>
        config.GetSection("Custodex:ApiKeys").Bind(opts.Keys));

builder.Services.Configure<PageSizeOptions>(o =>
    o.Max = builder.Configuration.GetValue("Custodex:MaxPageSize", PageSizeOptions.DefaultMax));

builder.Services.AddAuthorizationBuilder()
    .AddPolicy("Custodex:decide", p => p.RequireAuthenticatedUser()
        .RequireClaim("Custodex:role", "reader", "admin"))
    .AddPolicy("Custodex:manage", p => p.RequireAuthenticatedUser()
        .RequireClaim("Custodex:role", "admin"));
builder.Services.AddScoped<TenantContextAccessor>();
builder.Services.AddScoped<ITenantContextAccessor>(sp => sp.GetRequiredService<TenantContextAccessor>());

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
builder.Services.AddHsts(o =>
{
    o.Preload = true;
    o.IncludeSubDomains = true;
    o.MaxAge = TimeSpan.FromDays(365);
});

var app = builder.Build();

if (app.Configuration.GetValue("Custodex:ApplyMigrationsOnStartup", true))
{
    await using var conn = new NpgsqlConnection(connectionString);
    await conn.OpenAsync();
    await MigrationRunner.ApplyAsync(conn);
}

app.UseCustodexProblemDetails();

app.Use(SecurityHeadersMiddleware);

if (!app.Environment.IsDevelopment())
    app.UseHsts();

app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();
app.UseMiddleware<TenantResolutionMiddleware>();

app.MapStaticAssets();
app.MapDefaultEndpoints();
app.MapOpenApi();
app.MapGrpcService<DecisionGrpcService>().RequireAuthorization("Custodex:decide");
app.MapGrpcService<RelationsGrpcService>().RequireAuthorization("Custodex:manage");
app.MapGrpcService<SchemaGrpcService>().RequireAuthorization("Custodex:manage");
app.MapGrpcService<ProvisioningGrpcService>().RequireAuthorization("Custodex:manage");
app.MapCustodexRest();
app.MapCustodexStudio("Custodex:manage");

app.Run();

static Task SecurityHeadersMiddleware(HttpContext context, RequestDelegate next)
{
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["X-Frame-Options"] = "DENY";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers["Content-Security-Policy"] =
        "default-src 'self'; script-src 'self' 'wasm-unsafe-eval'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; font-src 'self' data:; connect-src 'self'; frame-ancestors 'none'; base-uri 'self'; form-action 'self'";
    return next(context);
}
