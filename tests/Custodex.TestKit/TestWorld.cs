using System.Runtime.CompilerServices;

using Bogus;

using Custodex.Abstractions;
using Custodex.Core.Conditions;
using Custodex.Core.Evaluation;
using Custodex.Storage.InMemory;

namespace Custodex.TestKit;

public sealed class TestWorld
{
    private readonly Randomizer _randomizer;
    private readonly NeutralIdentifiers _ids;
    private readonly HashSet<string> _used = new(StringComparer.Ordinal);

    private TestWorld(int seed)
    {
        _randomizer = new Randomizer(seed);
        _ids = new NeutralIdentifiers(_randomizer);

        UserType = Vend(() => _ids.Noun());
        GroupType = Vend(() => _ids.Noun());
        MemberRelation = Vend(() => _ids.Verb());

        Tenant = new TenantContext(Vend(() => _ids.Noun()), Vend(() => _ids.Noun()));
    }

    public static TestWorld New([CallerMemberName] string? test = null) =>
        new(Fnv1a(test ?? string.Empty));

    public string UserType { get; }

    public string GroupType { get; }

    public string MemberRelation { get; }

    public static string Version => "v1";

    public TenantContext Tenant { get; }

    public string EntityType() => Vend(_ids.Noun);

    public string Relation() => Vend(_ids.Verb);

    public string Permission() => Vend(_ids.Verb);

    public string ConditionName() => Vend(_ids.Adjective);

    public string ParamName() => Vend(_ids.Word);

    public string SubjectId() => Vend(() => _ids.Noun() + "-" + _ids.Token(4));

    public string ObjectId() => Vend(() => _ids.Noun() + "-" + _ids.Token(4));

    public SubjectRef User(string id) => new(UserType, id);

    public static SubjectRef Subject(string type, string id) => new(type, id);

    public static SubjectRef SubjectSet(string type, string id, string relation) => new(type, id, relation);

    public SubjectRef Member(string groupId) => new(GroupType, groupId, MemberRelation);

    public static EntityRef Object(string type, string id) => new(type, id);

    public static RelationTuple Tuple(
        string objType, string objId, string relation, SubjectRef subject, ConditionRef? condition = null) =>
        new(new EntityRef(objType, objId), relation, subject, condition);

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

    public CheckRequest Check(
        string objType,
        string objId,
        string permission,
        string subjectId,
        DateTimeOffset? now = null,
        IReadOnlyDictionary<string, object?>? context = null) =>
        Check(new EntityRef(objType, objId), permission, User(subjectId), now, context);

    public Task<EngineDrivenAuthorizer> BuildAsync(Schema schema, params RelationTuple[] tuples) =>
        BuildAsync(schema, new NullConditionEvaluator(), tuples, attributes: null);

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
