using Custodex.Abstractions;

using Microsoft.AspNetCore.Http;

namespace Custodex.AspNetCore;

internal sealed class RouteValueResolver : ICustodexObjectResolver
{
    public ValueTask<EntityRef?> ResolveAsync(CustodexResolutionContext context, CancellationToken cancellationToken)
    {
        var http = context.HttpContext;
        if (http is null)
            return ValueTask.FromResult<EntityRef?>(null);

        var binding = http.GetEndpoint()?.Metadata.GetMetadata<CustodexObjectBindingMetadata>();
        var type = binding?.ObjectType ?? context.ObjectType;

        foreach (var key in CandidateKeys(binding?.RouteKey, context.ObjectType))
        {
            if (http.Request.RouteValues.TryGetValue(key, out var value) && value?.ToString() is { Length: > 0 } id)
                return ValueTask.FromResult<EntityRef?>(new EntityRef(type, id));
        }

        return ValueTask.FromResult<EntityRef?>(null);
    }

    private static IEnumerable<string> CandidateKeys(string? overrideKey, string type)
    {
        if (overrideKey is { Length: > 0 })
            yield return overrideKey;
        yield return type + "Id";
        yield return type;
        yield return "id";
    }
}
