using Custodex.Abstractions;

namespace Custodex.AspNetCore;

internal sealed class ResourceIdResolver : ICustodexObjectResolver
{
    public bool TryResolve(CustodexResolutionContext context, out EntityRef entity)
    {
        if (context.Resource is string { Length: > 0 } id)
        {
            entity = new EntityRef(context.ObjectType, id);
            return true;
        }

        entity = default;
        return false;
    }
}
