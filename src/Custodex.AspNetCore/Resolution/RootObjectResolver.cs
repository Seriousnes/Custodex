using Custodex.Abstractions;

using Microsoft.Extensions.Options;

namespace Custodex.AspNetCore;

internal sealed class RootObjectResolver(IOptions<CustodexAuthorizationOptions> options) : ICustodexObjectResolver
{
    public ValueTask<EntityRef?> ResolveAsync(CustodexResolutionContext context, CancellationToken cancellationToken) =>
        ValueTask.FromResult(options.Value.RootObject?.Invoke(context));
}
