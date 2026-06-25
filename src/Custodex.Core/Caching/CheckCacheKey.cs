using Custodex.Abstractions;

namespace Custodex.Core.Caching;

/// <summary>
/// Derives the cache key that identifies a single Check decision. The key spans the tenant,
/// the active schema version, and the (object, permission, subject) triple, so a schema change
/// or any differing component yields a distinct entry.
/// </summary>
public static class CheckCacheKey
{
    private const char Sep = '';   // ASCII Unit Separator: never valid inside an identifier

    /// <summary>Builds the cache key for a Check decision.</summary>
    /// <param name="tenant">The store and tenant the decision belongs to.</param>
    /// <param name="schemaVersion">The version of the active schema the decision was evaluated against.</param>
    /// <param name="obj">The object the permission is checked on.</param>
    /// <param name="permission">The permission being checked.</param>
    /// <param name="subject">The subject the decision is about.</param>
    /// <returns>A stable key uniquely identifying this decision within the tenant.</returns>
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
