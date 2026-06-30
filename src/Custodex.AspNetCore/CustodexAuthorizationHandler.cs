using Custodex.Abstractions;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Custodex.AspNetCore;

internal sealed class CustodexAuthorizationHandler(
    IAuthorizer authorizer,
    ICustodexSubjectResolver subjectResolver,
    ICustodexTenantResolver tenantResolver,
    IEnumerable<ICustodexObjectResolver> objectResolvers,
    IRequestContextFactory requestContextFactory,
    IHttpContextAccessor httpContextAccessor,
    IOptions<CustodexAuthorizationOptions> options,
    ILogger<CustodexAuthorizationHandler> logger) : AuthorizationHandler<CustodexRequirement>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, CustodexRequirement requirement)
    {
        var http = httpContextAccessor.HttpContext;

        if (!subjectResolver.TryResolve(context.User, out var subject))
        {
            logger.LogDebug("Custodex denied {Type}:{Permission}: no subject", requirement.ObjectType, requirement.Permission);
            return;
        }

        if (!tenantResolver.TryResolve(context.User, http, out var tenant))
        {
            logger.LogDebug("Custodex denied {Type}:{Permission}: no tenant", requirement.ObjectType, requirement.Permission);
            return;
        }

        var resolution = new CustodexResolutionContext(
            requirement.ObjectType, requirement.Permission, context.User, context.Resource, http, tenant);

        EntityRef entity = default;
        var found = false;
        foreach (var resolver in objectResolvers)
        {
            if (resolver.TryResolve(resolution, out entity))
            {
                found = true;
                break;
            }
        }

        if (!found)
        {
            logger.LogDebug("Custodex denied {Type}:{Permission}: no object resolved", requirement.ObjectType, requirement.Permission);
            return;
        }

        var requestContext = requestContextFactory.Create(subject, resolution);
        var ct = http?.RequestAborted ?? CancellationToken.None;

        try
        {
            var result = await authorizer.CheckAsync(
                new CheckRequest(tenant, entity, requirement.Permission, subject, requestContext), ct);
            if (result.Allowed)
                context.Succeed(requirement);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (!options.Value.ThrowOnEvaluationError)
        {
            logger.LogError(ex, "Custodex evaluation failed for {Type}:{Permission}; denying", requirement.ObjectType, requirement.Permission);
        }
    }
}
