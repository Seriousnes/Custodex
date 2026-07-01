using System.Security.Claims;

using Custodex.Abstractions;

using Microsoft.Extensions.Options;

namespace Custodex.AspNetCore;

internal sealed class ClaimsCustodexSubjectResolver(IOptions<CustodexAuthorizationOptions> options) : ICustodexSubjectResolver
{
    public ValueTask<SubjectRef?> ResolveAsync(ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        var id = user.FindFirstValue(options.Value.SubjectIdClaim);
        return string.IsNullOrEmpty(id)
            ? ValueTask.FromResult<SubjectRef?>(null)
            : ValueTask.FromResult<SubjectRef?>(new SubjectRef(options.Value.SubjectType, id));
    }
}
