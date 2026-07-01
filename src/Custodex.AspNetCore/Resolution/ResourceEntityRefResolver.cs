using Custodex.Abstractions;

namespace Custodex.AspNetCore;

internal sealed class ResourceEntityRefResolver : ICustodexObjectResolver
{
    public ValueTask<EntityRef?> ResolveAsync(CustodexResolutionContext context, CancellationToken cancellationToken) =>
        ValueTask.FromResult<EntityRef?>(context.Resource is EntityRef e ? e : null);
}
