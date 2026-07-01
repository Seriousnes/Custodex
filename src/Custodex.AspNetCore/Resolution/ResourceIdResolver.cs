using Custodex.Abstractions;

namespace Custodex.AspNetCore;

internal sealed class ResourceIdResolver : ICustodexObjectResolver
{
    public ValueTask<EntityRef?> ResolveAsync(CustodexResolutionContext context, CancellationToken cancellationToken) =>
        ValueTask.FromResult<EntityRef?>(
            context.Resource is string { Length: > 0 } id ? new EntityRef(context.ObjectType, id) : null);
}
