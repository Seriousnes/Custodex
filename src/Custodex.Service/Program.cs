using System.Security.Claims;
using System.Threading.RateLimiting;

using Custodex.AspNetCore;
using Custodex.Core;
using Custodex.Service;
using Custodex.Service.Health;
using Custodex.Storage.Postgres;
using Custodex.Studio;

using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Npgsql;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddOpenTelemetry()
    .WithTracing(t => t.AddCustodexInstrumentation())
    .WithMetrics(m => m.AddCustodexInstrumentation());

builder.Services.AddCustodexService(builder.Configuration);
builder.Services.AddAuthentication("Custodex-any");
builder.Services.AddOpenApi(o => o.AddCustodexApiDocumentation());

var rateLimitPermitLimit = builder.Configuration.GetValue("Custodex:RateLimit:PermitLimit", RateLimitOptions.DefaultPermitLimit);
var rateLimitWindowSeconds = builder.Configuration.GetValue("Custodex:RateLimit:WindowSeconds", RateLimitOptions.DefaultWindowSeconds);

builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(RateLimitCallerKey(httpContext), _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = rateLimitPermitLimit,
            Window = TimeSpan.FromSeconds(rateLimitWindowSeconds),
        }));
});

builder.WebHost.ConfigureKestrel(o =>
    o.Limits.MaxRequestBodySize = builder.Configuration.GetValue("Custodex:MaxRequestBodyBytes", 10_000_000L));

var connectionString = builder.Configuration.GetConnectionString("Custodex")
    ?? builder.Configuration["Custodex:ConnectionString"]
    ?? throw new InvalidOperationException("Custodex:ConnectionString is required.");

builder.Services.AddCustodex().UsePostgres(connectionString);
builder.Services.AddCustodexStudioPostgresViewStore(connectionString);
builder.Services.AddCustodexStudio();

builder.Services.TryAddSingleton(TimeProvider.System);
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
app.UseRateLimiter();
app.UseAntiforgery();
app.UseCustodexTenantResolution();

app.MapStaticAssets();
app.MapDefaultEndpoints();
if (app.Environment.IsDevelopment())
    app.MapOpenApi();
app.MapCustodex();
app.MapCustodexStudio("Custodex:manage");

app.Run();

static string RateLimitCallerKey(HttpContext context)
{
    var store = context.User.FindFirst(CustodexClaimTypes.Store)?.Value;
    var subject = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
    if (store is not null && subject is not null)
        return $"{store}:{subject}";
    return context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
}

static Task SecurityHeadersMiddleware(HttpContext context, RequestDelegate next)
{
    context.Response.OnStarting(static state =>
    {
        var headers = ((HttpContext)state).Response.Headers;
        headers["X-Content-Type-Options"] = "nosniff";
        headers["X-Frame-Options"] = "DENY";
        headers["Referrer-Policy"] = "no-referrer";
        headers["Content-Security-Policy"] =
            "default-src 'self'; script-src 'self' 'wasm-unsafe-eval'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; font-src 'self' data:; connect-src 'self'; frame-ancestors 'none'; base-uri 'self'; form-action 'self'";
        return Task.CompletedTask;
    }, context);
    return next(context);
}
