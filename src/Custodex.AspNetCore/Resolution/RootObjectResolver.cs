using Custodex.Abstractions;

using Microsoft.Extensions.Options;

namespace Custodex.AspNetCore;

internal sealed class RootObjectResolver(IOptions<CustodexAuthorizationOptions> options) : ICustodexObjectResolver
{
    public bool TryResolve(CustodexResolutionContext context, out EntityRef entity)
    {
        if (options.Value.RootObject?.Invoke(context) is { } resolved)
        {
            entity = resolved;
            return true;
        }

        entity = default;
        return false;
    }
}
