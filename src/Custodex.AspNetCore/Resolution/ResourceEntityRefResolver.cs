using Custodex.Abstractions;

namespace Custodex.AspNetCore;

internal sealed class ResourceEntityRefResolver : ICustodexObjectResolver
{
    public bool TryResolve(CustodexResolutionContext context, out EntityRef entity)
    {
        if (context.Resource is EntityRef e)
        {
            entity = e;
            return true;
        }

        entity = default;
        return false;
    }
}
