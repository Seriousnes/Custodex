using Custodex.Abstractions;
using Custodex.Core.Conditions;
using Custodex.Core.Evaluation;

using Microsoft.Data.SqlClient;

namespace Custodex.Storage.SqlServer.Index;

/// <summary>
/// Performs a full rebuild of the reverse index for a (store, tenant) pair by probing every
/// candidate (subject, permission, object) triple with the structural pointwise Check and
/// persisting the granted ones. The rebuild is idempotent: it clears prior rows and markers
/// before writing, so running it twice produces the same result as running it once.
/// Writes are issued through the caller's unit of work so the entire rebuild commits atomically.
/// </summary>
public sealed class ReverseIndexRebuilder(
    string connectionString,
    ISchemaStore schemas,
    IRelationStore relations,
    IAttributeStore attributes,
    IIndexStore index)
{
    private readonly string _connectionString = connectionString;

    /// <summary>
    /// Rebuilds the reverse index for <paramref name="t"/> transactionally within
    /// <paramref name="uow"/>. Throws <see cref="UnknownTypeException"/> when no active schema
    /// exists for the store.
    /// </summary>
    public async Task RebuildAsync(TenantContext t, IUnitOfWork uow, CancellationToken ct = default)
    {
        var schema = await schemas.GetActiveAsync(t.Store, ct)
            ?? throw new UnknownTypeException($"<no active schema for store '{t.Store}'>");
        var schemaIndex = new SchemaIndex(schema);

        await index.ClearAsync(t, uow, ct);

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        var inputs = await RebuildEnumeration.LoadAsync(conn, null, t, ct);

        var authorizer = new EngineDrivenAuthorizer(schemas, relations, attributes, new NullConditionEvaluator());

        var subjects = new List<SubjectRef>(inputs.Users.Count + 1);
        foreach (var uid in inputs.Users) subjects.Add(new SubjectRef("user", uid));
        subjects.Add(new SubjectRef("user", "*"));

        var context = new RequestContext(DateTimeOffset.UnixEpoch,
            new SubjectRef("user", "<rebuild>"), new Dictionary<string, object?>());

        var batch = new List<ReverseIndexRow>(capacity: 256);

        foreach (var typeDef in schema.Types)
        {
            if (typeDef.Permissions.Count == 0) continue;
            if (!inputs.ObjectIdsByType.TryGetValue(typeDef.Name, out var objectIds)) continue;

            foreach (var perm in typeDef.Permissions)
            foreach (var oid in objectIds)
            {
                var obj = new EntityRef(typeDef.Name, oid);
                foreach (var subject in subjects)
                {
                    var grant = await authorizer.CheckStructuralAsync(
                        schemaIndex, t, obj, perm.Name, subject, context, ct);
                    if (!grant.Granted) continue;
                    batch.Add(new ReverseIndexRow(subject.ToString(), perm.Name, typeDef.Name, oid, grant.Conditioned));
                }
            }
        }

        await index.UpsertAsync(t, schema.Version, batch, uow, ct);
        await index.MarkBuiltAsync(t, schema.Version, uow, ct);
    }
}
