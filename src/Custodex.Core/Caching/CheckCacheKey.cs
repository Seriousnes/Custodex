using Custodex.Abstractions;

namespace Custodex.Core.Caching;

public static class CheckCacheKey
{
    private const char Sep = '';   // ASCII Unit Separator: never valid inside an identifier

    public static string Build(
        TenantContext tenant, string schemaVersion, EntityRef obj, string permission, SubjectRef subject)
    {
        var subj = subject.Relation is null
            ? $"{subject.Type}:{subject.Id}"
            : $"{subject.Type}:{subject.Id}#{subject.Relation}";
        return string.Join(Sep,
            "Custodex.check",
            tenant.Store,
            tenant.Tenant,
            schemaVersion,
            $"{obj.Type}:{obj.Id}",
            permission,
            subj);
    }
}
