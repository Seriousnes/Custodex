using Custodex.Abstractions;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Custodex.AspNetCore;

internal sealed class CustodexAuthorizationHandler(
    IAuthorizer authorizer,
    ICustodexDecisionCache decisionCache,
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
        var ct = http?.RequestAborted ?? CancellationToken.None;

        try
        {
            var subject = await subjectResolver.ResolveAsync(context.User, ct);
            if (subject is null)
            {
                logger.LogDebug("Custodex denied {Type}:{Permission}: no subject", requirement.ObjectType, requirement.Permission);
                return;
            }

            var tenant = await tenantResolver.ResolveAsync(context.User, http, ct);
            if (tenant is null)
            {
                logger.LogDebug("Custodex denied {Type}:{Permission}: no tenant", requirement.ObjectType, requirement.Permission);
                return;
            }

            var resolution = new CustodexResolutionContext(
                requirement.ObjectType, requirement.Permission, context.User, context.Resource, http, tenant.Value);

            if (requirement.AnyObject)
            {
                var anyContext = requestContextFactory.Create(subject.Value, resolution);
                var page = await authorizer.ListObjectsAsync(
                    new ListObjectsRequest(
                        tenant.Value, subject.Value, requirement.ObjectType, requirement.Permission, anyContext, PageSize: 1),
                    ct);
                if (page.ObjectIds.Count > 0)
                    context.Succeed(requirement);
                return;
            }

            EntityRef? entity = null;
            foreach (var resolver in objectResolvers)
            {
                entity = await resolver.ResolveAsync(resolution, ct);
                if (entity is not null)
                    break;
            }

            if (entity is null)
            {
                logger.LogDebug("Custodex denied {Type}:{Permission}: no object resolved", requirement.ObjectType, requirement.Permission);
                return;
            }

            var requestContext = requestContextFactory.Create(subject.Value, resolution);

            var result = await decisionCache.CheckAsync(
                new CheckRequest(tenant.Value, entity.Value, requirement.Permission, subject.Value, requestContext), ct);
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
