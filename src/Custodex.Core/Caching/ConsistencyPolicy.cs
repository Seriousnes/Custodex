using Custodex.Abstractions;

namespace Custodex.Core.Caching;

internal static class ConsistencyPolicy
{
    public static (bool BypassCache, long? FloorEpoch) Resolve(Consistency? consistency, TenantContext tenant)
    {
        if (consistency is null)
            return (false, null);

        return consistency.Mode switch
        {
            ConsistencyMode.FullyConsistent => (true, null),
            ConsistencyMode.AtLeastAsFresh => (false, RequiredEpoch(consistency, tenant)),
            _ => (false, null),
        };
    }

    public static void Validate(Consistency? consistency, TenantContext tenant) => _ = Resolve(consistency, tenant);

    private static long RequiredEpoch(Consistency consistency, TenantContext tenant)
    {
        var token = consistency.Token
            ?? throw new InvalidConsistencyTokenException("At-least-as-fresh consistency requires a token.");
        return token.EpochFor(tenant);
    }
}
