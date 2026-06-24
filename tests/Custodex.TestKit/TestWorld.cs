using System.Runtime.CompilerServices;
using Bogus;
using Custodex.Abstractions;
using Custodex.Core.Conditions;
using Custodex.Core.Evaluation;
using Custodex.Storage.InMemory;

namespace Custodex.TestKit;

/// <summary>
/// Per-test façade that vends de-domained, deterministic authorization vocabulary and absorbs the
/// store-wiring boilerplate shared across evaluation tests. Create one with <see cref="New"/>; the
/// sequence it produces is a pure function of the test name, so it is stable across runs and processes
/// and safe under xUnit parallelism (each world owns its own <see cref="Randomizer"/>; no global seed).
/// </summary>
public sealed class TestWorld
{
    private readonly Randomizer _randomizer;
    private readonly NeutralIdentifiers _ids;
    private readonly HashSet<string> _used = new(StringComparer.Ordinal);

    private TestWorld(int seed)
    {
        _randomizer = new Randomizer(seed);
        _ids = new NeutralIdentifiers(_randomizer);

        // De-domained structural terms, generated once and cached. They share the uniqueness set
        // (Vend reserves them), so later EntityType()/Relation()/Permission() vends can never collide
        // with them — a schema can use UserType as a subject type and EntityType() as the object type.
        UserType = Vend(() => _ids.Noun());
        GroupType = Vend(() => _ids.Noun());
        MemberRelation = Vend(() => _ids.Verb());

        Tenant = new TenantContext(Vend(() => _ids.Noun()), Vend(() => _ids.Noun()));
    }

    /// <summary>
    /// Creates a world seeded from a deterministic FNV-1a hash of the calling test's name. Two worlds
    /// created with the same name produce identical sequences; different names produce different ones.
    /// </summary>
    public static TestWorld New([CallerMemberName] string? test = null) =>
        new(Fnv1a(test ?? string.Empty));

    /// <summary>
    /// The de-domained entity type for principals, generated once and stable within this world.
    /// The <see cref="User"/> factory binds to it, so declaring a schema with
    /// <c>s.Type(world.UserType)</c> agrees with <c>world.User(id)</c> tuples by construction.
    /// </summary>
    public string UserType { get; }

    /// <summary>The de-domained entity type for groups, generated once and stable within this world.</summary>
    public string GroupType { get; }

    /// <summary>The de-domained relation naming group membership, generated once and stable within this world.</summary>
    public string MemberRelation { get; }

    /// <summary>A simple, stable schema version label. Not domain vocabulary.</summary>
    public string Version => "v1";

    /// <summary>A stable store/tenant pair generated once for this world.</summary>
    public TenantContext Tenant { get; }

    /// <summary>Vends a fresh entity type token, unique within this world.</summary>
    public string EntityType() => Vend(() => _ids.Noun());

    /// <summary>Vends a fresh relation token, unique within this world.</summary>
    public string Relation() => Vend(() => _ids.Verb());

    /// <summary>Vends a fresh permission token, unique within this world.</summary>
    public string Permission() => Vend(() => _ids.Verb());

    /// <summary>Vends a fresh condition name token, unique within this world.</summary>
    public string ConditionName() => Vend(() => _ids.Adjective());

    /// <summary>Vends a fresh parameter name token, unique within this world.</summary>
    public string ParamName() => Vend(() => _ids.Word());

    /// <summary>Vends a fresh subject id token, unique within this world.</summary>
    public string SubjectId() => Vend(() => _ids.Noun() + "-" + _ids.Token(4));

    /// <summary>Vends a fresh object id token, unique within this world.</summary>
    public string ObjectId() => Vend(() => _ids.Noun() + "-" + _ids.Token(4));

    /// <summary>A subject referencing this world's <see cref="UserType"/>.</summary>
    public SubjectRef User(string id) => new(UserType, id);

    /// <summary>A direct subject of an arbitrary type.</summary>
    public SubjectRef Subject(string type, string id) => new(type, id);

    /// <summary>A subject-set reference (e.g. <c>group:G#member</c>).</summary>
    public SubjectRef SubjectSet(string type, string id, string relation) => new(type, id, relation);

    /// <summary>A subject-set over this world's group membership (<see cref="GroupType"/>#<see cref="MemberRelation"/>).</summary>
    public SubjectRef Member(string groupId) => new(GroupType, groupId, MemberRelation);

    /// <summary>An entity reference of an arbitrary type.</summary>
    public EntityRef Object(string type, string id) => new(type, id);

    /// <summary>A relation tuple binding an object to a subject under <paramref name="relation"/>.</summary>
    public RelationTuple Tuple(
        string objType, string objId, string relation, SubjectRef subject, ConditionRef? condition = null) =>
        new(new EntityRef(objType, objId), relation, subject, condition);

    /// <summary>
    /// A check request. Defaults <paramref name="now"/> to <see cref="DateTimeOffset.UnixEpoch"/> and
    /// <paramref name="context"/> to an empty attribute bag, keeping output deterministic.
    /// </summary>
    public CheckRequest Check(
        EntityRef obj,
        string permission,
        SubjectRef subject,
        DateTimeOffset? now = null,
        IReadOnlyDictionary<string, object?>? context = null)
    {
        var attributes = context ?? EmptyAttributes;
        var ctx = new RequestContext(now ?? DateTimeOffset.UnixEpoch, subject, attributes);
        return new CheckRequest(Tenant, obj, permission, subject, ctx);
    }

    /// <summary>
    /// A check request for a <see cref="User"/> subject identified by <paramref name="subjectId"/>.
    /// Defaults <paramref name="now"/> to <see cref="DateTimeOffset.UnixEpoch"/>.
    /// </summary>
    public CheckRequest Check(
        string objType,
        string objId,
        string permission,
        string subjectId,
        DateTimeOffset? now = null,
        IReadOnlyDictionary<string, object?>? context = null) =>
        Check(new EntityRef(objType, objId), permission, User(subjectId), now, context);

    /// <summary>
    /// Wires in-memory stores, activates <paramref name="schema"/> for this world's tenant store, writes
    /// <paramref name="tuples"/>, commits, and returns an authorizer using <see cref="NullConditionEvaluator"/>.
    /// </summary>
    public Task<EngineDrivenAuthorizer> BuildAsync(Schema schema, params RelationTuple[] tuples) =>
        BuildAsync(schema, new NullConditionEvaluator(), tuples, attributes: null);

    /// <summary>
    /// Wires in-memory stores with a custom <paramref name="conditions"/> evaluator and optional object
    /// <paramref name="attributes"/>, activates <paramref name="schema"/>, writes <paramref name="tuples"/>,
    /// commits, and returns the authorizer. For ABAC tests that need attribute seeding.
    /// </summary>
    public async Task<EngineDrivenAuthorizer> BuildAsync(
        Schema schema,
        IConditionEvaluator conditions,
        IReadOnlyList<RelationTuple> tuples,
        IReadOnlyList<(EntityRef Object, IReadOnlyDictionary<string, object?> Attributes)>? attributes = null)
    {
        var schemaStore = new InMemorySchemaStore();
        var relations = new InMemoryRelationStore();
        var attributeStore = new InMemoryAttributeStore();
        var uow = new NoOpUnitOfWork();

        await schemaStore.SetActiveAsync(Tenant.Store, schema, uow);
        if (tuples.Count > 0)
            await relations.WriteAsync(Tenant, tuples, [], uow);
        if (attributes is not null)
            foreach (var (obj, attrs) in attributes)
                await attributeStore.SetAsync(Tenant, obj, attrs, uow);
        await uow.CommitAsync();

        return new EngineDrivenAuthorizer(schemaStore, relations, attributeStore, conditions);
    }

    private static readonly IReadOnlyDictionary<string, object?> EmptyAttributes =
        new Dictionary<string, object?>(StringComparer.Ordinal);

    private string Vend(Func<string> draw)
    {
        for (var attempt = 0; attempt < 64; attempt++)
        {
            var candidate = draw();
            if (_used.Add(candidate))
                return candidate;
        }

        // Generators are finite; guarantee termination by appending an increasing suffix.
        var baseValue = draw();
        for (var suffix = 1; ; suffix++)
        {
            var candidate = baseValue + "-" + suffix.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (_used.Add(candidate))
                return candidate;
        }
    }

    private static int Fnv1a(string value)
    {
        const uint offsetBasis = 2166136261;
        const uint prime = 16777619;
        var hash = offsetBasis;
        foreach (var ch in value)
        {
            hash ^= ch;
            hash *= prime;
        }

        return unchecked((int)hash);
    }
}
