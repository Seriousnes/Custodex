using System.Security.Claims;

using Custodex.Core;
using Custodex.Service.Auth;
using Custodex.Service.Health;
using Custodex.Service.Rest;
using Custodex.Service.Services;
using Custodex.Service.Tenancy;
using Custodex.Storage.Postgres;
using Custodex.Studio;

using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi;

using Npgsql;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddOpenTelemetry()
    .WithTracing(t => t.AddCustodexInstrumentation())
    .WithMetrics(m => m.AddCustodexInstrumentation());
builder.Services.AddGrpc(o => o.Interceptors.Add<CustodexExceptionInterceptor>());
builder.Services.AddSingleton<CustodexExceptionInterceptor>();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("api", new OpenApiInfo { Title = "Custodex Authorization API", Version = "1.0" });
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
builder.Services.AddHealthChecks()
    .AddCheck<PostgresReadyHealthCheck>("postgres", tags: ["ready"]);

var app = builder.Build();

if (app.Configuration.GetValue("Custodex:ApplyMigrationsOnStartup", true))
{
    await using var conn = new NpgsqlConnection(connectionString);
    await conn.OpenAsync();
    await MigrationRunner.ApplyAsync(conn);
}

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(c => c.SwaggerEndpoint("/swagger/api/swagger.json", "Custodex Authorization API"));
}

app.UseCustodexProblemDetails();

app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();
app.UseMiddleware<TenantResolutionMiddleware>();

app.MapStaticAssets();
app.MapDefaultEndpoints();
app.MapGrpcService<DecisionGrpcService>().RequireAuthorization("Custodex:decide");
app.MapGrpcService<RelationsGrpcService>().RequireAuthorization("Custodex:manage");
app.MapGrpcService<SchemaGrpcService>().RequireAuthorization("Custodex:manage");
app.MapGrpcService<ProvisioningGrpcService>().RequireAuthorization("Custodex:manage");
app.MapCustodexRest();
app.MapCustodexStudio();

app.Run();
