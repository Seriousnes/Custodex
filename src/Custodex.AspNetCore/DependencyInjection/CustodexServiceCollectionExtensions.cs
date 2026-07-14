using System.Security.Claims;

using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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
    /// Call <see cref="CustodexApplicationBuilderExtensions.MapCustodex"/> to map the endpoints.
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

        var jwtSection = configuration.GetSection($"{sectionName}:Jwt");
        var signingKeyB64 = jwtSection["SigningKey"];

        services
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

        services.AddOptions<ApiKeyOptions>("ApiKey")
            .Configure(opts => configuration.GetSection($"{sectionName}:ApiKeys").Bind(opts.Keys));

        services.AddAuthorizationBuilder()
            .AddPolicy(DecidePolicy, p => p.RequireAuthenticatedUser()
                .RequireClaim("Custodex:role", "reader", "admin"))
            .AddPolicy(ManagePolicy, p => p.RequireAuthenticatedUser()
                .RequireClaim("Custodex:role", "admin"));

        services.AddScoped<TenantContextAccessor>();
        services.AddScoped<ITenantContextAccessor>(sp => sp.GetRequiredService<TenantContextAccessor>());

        return services;
    }
}
