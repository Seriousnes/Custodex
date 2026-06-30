using System.Security.Claims;

using Custodex.Abstractions;

using Microsoft.Extensions.Options;

namespace Custodex.AspNetCore;

internal sealed class ClaimsCustodexSubjectResolver(IOptions<CustodexAuthorizationOptions> options) : ICustodexSubjectResolver
{
    public bool TryResolve(ClaimsPrincipal user, out SubjectRef subject)
    {
        subject = default;
        var id = user.FindFirstValue(options.Value.SubjectIdClaim);
        if (string.IsNullOrEmpty(id))
            return false;

        subject = new SubjectRef(options.Value.SubjectType, id);
        return true;
    }
}
