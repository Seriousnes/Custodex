using System.Security.Claims;

using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;

namespace Custodex.AspNetCore;

/// <summary>
/// Registers the Custodex service surface: the gRPC endpoints, the REST endpoints, the
/// <c>ApiKey</c> and JWT bearer authentication schemes, the <c>Custodex:decide</c> and
/// <c>Custodex:manage</c> authorization policies, and request-scoped tenant resolution.
/// </summary>
public static class CustodexServiceCollectionExtensions
{
    /// <summary>The authorization policy that guards the decision surface (Check, BatchCheck, ListObjects, ListSubjects).</summary>
    public const string DecidePolicy = "Custodex:decide";

    /// <summary>The authorization policy that guards the management and provisioning surface.</summary>
    public const string ManagePolicy = "Custodex:manage";

    /// <summary>
    /// Registers everything a host needs to expose the Custodex engine over gRPC and REST behind
    /// authentication. Assumes the engine itself (<c>IAuthorizer</c> and the management seams) is
    /// already registered via <c>AddCustodex()</c>. Binds authentication from the given configuration:
    /// <c>{section}:ApiKeys</c> for store-scoped API keys and <c>{section}:Jwt</c> for bearer tokens.
    /// The request-size bounds <c>{section}:MaxPageSize</c> and <c>{section}:MaxBatchItems</c> cap the
    /// list and batch surfaces. Call <see cref="CustodexApplicationBuilderExtensions.MapCustodex"/> to
    /// map the endpoints.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">Configuration carrying the authentication settings.</param>
    /// <param name="sectionName">The configuration section root. Defaults to <c>Custodex</c>.</param>
    /// <returns>The service collection, for chaining.</returns>
    public static IServiceCollection AddCustodexService(
        this IServiceCollection services, IConfiguration configuration, string sectionName = "Custodex")
    {
        services.AddGrpc(o => o.Interceptors.Add<CustodexExceptionInterceptor>());
        services.AddSingleton<CustodexExceptionInterceptor>();
        services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new ObjectJsonConverter()));

        var jwtSection = configuration.GetSection($"{sectionName}:Jwt");
        var signingKeyB64 = jwtSection["SigningKey"];

        if (!string.IsNullOrEmpty(signingKeyB64))
        {
            var configuredIssuer = jwtSection["Issuer"];
            var configuredAudience = jwtSection["Audience"];
            if (string.IsNullOrEmpty(configuredIssuer) || string.IsNullOrEmpty(configuredAudience))
                throw new InvalidOperationException(
                    $"{sectionName}:Jwt:Issuer and {sectionName}:Jwt:Audience are required when a symmetric SigningKey is configured.");
        }

        services
            .AddAuthentication("Custodex-any")
            .AddPolicyScheme("Custodex-any", "ApiKey or Bearer", o =>
            {
                o.ForwardDefaultSelector = ctx =>
                    ctx.Request.Headers.ContainsKey(CustodexHeaders.ApiKey) ? "ApiKey" : JwtBearerDefaults.AuthenticationScheme;
                o.ForwardChallenge = JwtBearerDefaults.AuthenticationScheme;
            })
            .AddScheme<ApiKeyOptions, ApiKeyAuthenticationHandler>("ApiKey", _ => { })
            .AddJwtBearer(jwt =>
            {
                var roleClaim = jwtSection["RoleClaim"] ?? CustodexClaimTypes.Role;
                var storeClaim = jwtSection["StoreClaim"] ?? CustodexClaimTypes.Store;

                if (!string.IsNullOrEmpty(signingKeyB64))
                {
                    var key = new SymmetricSecurityKey(Convert.FromBase64String(signingKeyB64));
                    var issuer = jwtSection["Issuer"];
                    var audience = jwtSection["Audience"];
                    jwt.TokenValidationParameters = new TokenValidationParameters
                    {
                        ValidateIssuerSigningKey = true,
                        IssuerSigningKey = key,
                        ValidateIssuer = true,
                        ValidIssuer = issuer,
                        ValidateAudience = true,
                        ValidAudience = audience,
                        ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
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

                jwt.Events = new JwtBearerEvents
                {
                    OnTokenValidated = ctx =>
                    {
                        if (!string.Equals(storeClaim, CustodexClaimTypes.Store, StringComparison.Ordinal)
                            && ctx.Principal?.Identities.FirstOrDefault() is { } identity
                            && identity.FindFirst(CustodexClaimTypes.Store) is null
                            && identity.FindFirst(storeClaim) is { Value.Length: > 0 } source)
                        {
                            identity.AddClaim(new Claim(CustodexClaimTypes.Store, source.Value));
                        }
                        return Task.CompletedTask;
                    },
                    OnAuthenticationFailed = ctx =>
                    {
                        ctx.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>()
                            .CreateLogger("Custodex.AspNetCore.JwtBearer")
                            .LogWarning(
                                ctx.Exception,
                                "JWT authentication failed from {RemoteIp}.",
                                ctx.HttpContext.Connection.RemoteIpAddress);
                        return Task.CompletedTask;
                    },
                    OnChallenge = ctx =>
                    {
                        ctx.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>()
                            .CreateLogger("Custodex.AspNetCore.JwtBearer")
                            .LogWarning(
                                "JWT authentication challenge issued from {RemoteIp}: {Reason}.",
                                ctx.HttpContext.Connection.RemoteIpAddress,
                                ctx.AuthenticateFailure?.Message ?? ctx.ErrorDescription ?? "no token presented");
                        return Task.CompletedTask;
                    },
                };
            });

        services.AddOptions<ApiKeyOptions>("ApiKey")
            .Configure(opts => configuration.GetSection($"{sectionName}:ApiKeys").Bind(opts.Keys))
            .Validate(
                opts => opts.Keys.All(k =>
                    !string.IsNullOrWhiteSpace(k.Key) && !string.IsNullOrWhiteSpace(k.Store) && !string.IsNullOrWhiteSpace(k.Role)),
                $"{sectionName}:ApiKeys entries must have non-empty Key, Store, and Role values.")
            .ValidateOnStart();

        services.Configure<PageSizeOptions>(o =>
            o.Max = configuration.GetValue($"{sectionName}:MaxPageSize", PageSizeOptions.DefaultMax));
        services.Configure<BatchCheckOptions>(o =>
            o.MaxItems = configuration.GetValue($"{sectionName}:MaxBatchItems", BatchCheckOptions.DefaultMaxItems));

        services.AddAuthorizationBuilder()
            .AddPolicy(DecidePolicy, p => p.RequireAuthenticatedUser()
                .RequireRole("reader", "admin"))
            .AddPolicy(ManagePolicy, p => p.RequireAuthenticatedUser()
                .RequireRole("admin"));

        services.AddScoped<TenantContextAccessor>();
        services.AddScoped<ITenantContextAccessor>(sp => sp.GetRequiredService<TenantContextAccessor>());

        return services;
    }
}
