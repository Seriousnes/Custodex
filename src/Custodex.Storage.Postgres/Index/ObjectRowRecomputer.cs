using Custodex.Abstractions;
using Custodex.Core.Conditions;
using Custodex.Core.Evaluation;

namespace Custodex.Storage.Postgres.Index;

/// <summary>
/// Recomputes one object's reverse-index rows exactly as the full rebuild would, scoped to a
/// single object. For each permission on the object's type and each candidate subject
/// (<c>user:{id}</c> ∪ <c>user:*</c>), probes the unconditioned structural Check and keeps the
/// granted ones with their conditioned flag. Sharing this logic with the full rebuild is what
/// makes incremental maintenance produce results identical to a full rebuild by construction.
/// </summary>
public sealed class ObjectRowRecomputer(
    IRelationStore relations,
    IAttributeStore attributes)
{
    /// <summary>
    /// Returns the structural reverse-index rows that should exist for <paramref name="obj"/>
    /// under <paramref name="t"/>, computed by probing every (permission, subject) pair via the
    /// engine-driven structural Check. Throws <see cref="UnknownTypeException"/> when
    /// <paramref name="obj"/>'s type is not present in <paramref name="index"/>.
    /// </summary>
    public async Task<IReadOnlyList<ReverseIndexRow>> RecomputeAsync(
        SchemaIndex index, ISchemaStore schemas, TenantContext t, EntityRef obj,
        IReadOnlyList<string> candidateUsers, CancellationToken ct = default)
    {
        var typeDef = index.Type(obj.Type);
        if (typeDef.Permissions.Count == 0) return [];

        var authorizer = new EngineDrivenAuthorizer(schemas, relations, attributes, new NullConditionEvaluator());

        var subjects = new List<SubjectRef>(candidateUsers.Count + 1);
        foreach (var uid in candidateUsers) subjects.Add(new SubjectRef("user", uid));
        subjects.Add(new SubjectRef("user", "*"));

        var context = new RequestContext(DateTimeOffset.UnixEpoch,
            new SubjectRef("user", "<maintain>"), new Dictionary<string, object?>());

        var rows = new List<ReverseIndexRow>();
        foreach (var perm in typeDef.Permissions)
        foreach (var subject in subjects)
        {
            var grant = await authorizer.CheckStructuralAsync(index, t, obj, perm.Name, subject, context, ct);
            if (grant.Granted)
                rows.Add(new ReverseIndexRow(subject.ToString(), perm.Name, obj.Type, obj.Id, grant.Conditioned));
        }
        return rows;
    }
}
