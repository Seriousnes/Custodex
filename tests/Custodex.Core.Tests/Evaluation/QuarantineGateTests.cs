using Custodex.Abstractions;
using Custodex.Core;
using Custodex.Core.Conditions;
using Custodex.Core.Evaluation;
using Custodex.Storage.InMemory;
using Shouldly;
using Xunit;

namespace Custodex.Core.Tests.Evaluation;

public class QuarantineGateTests
{
    private static readonly TenantContext T = new("zoo", "t1");

    private static Schema Build() => new SchemaBuilder("v1")
        .Type("group", t => t.Relation("member", s => s.User().SubjectSet("group", "member")))
        .Type("enclosure", t => t
            .Relation("is_quarantine", s => s.Wildcard("user"))
            .Permission("is_quarantine", p => p.Relation("is_quarantine")))
        .Type("animal", t => t
            .Relation("can_access", s => s.User().SubjectSet("group", "member"))
            .Relation("enclosure", s => s.Type("enclosure"))
            .Relation("vet_member", s => s.SubjectSet("group", "member"))
            .Relation("vet_nurse_member", s => s.SubjectSet("group", "member"))
            .Relation("trained_member", s => s.SubjectSet("group", "member"))
            .Permission("access", p => p
                .Union(b => b.Relation("can_access").Exclude(x => x.Arrow("enclosure", "is_quarantine")))
                .Union(b => b
                    .Arrow("enclosure", "is_quarantine")
                    .Intersect(x => x.Relation("vet_member").Union(y => y.Relation("vet_nurse_member")))
                    .Intersect(x => x.Relation("trained_member")))))
        .Build();

    private static async Task<EngineDrivenAuthorizer> NewAsync(Schema schema, params RelationTuple[] tuples)
    {
        var schemaStore = new InMemorySchemaStore();
        var relations = new InMemoryRelationStore();
        var attributes = new InMemoryAttributeStore();
        var uow = new NoOpUnitOfWork();
        await schemaStore.SetActiveAsync(T.Store, schema, uow);
        await relations.WriteAsync(T, tuples, Array.Empty<RelationTuple>(), uow);
        await uow.CommitAsync();
        return new EngineDrivenAuthorizer(schemaStore, relations, attributes, new NullConditionEvaluator());
    }

    private static RelationTuple Tuple(string ot, string oid, string rel, SubjectRef s) =>
        new(new EntityRef(ot, oid), rel, s);

    private static CheckRequest Access(string animal, string user) => new(
        T, new EntityRef("animal", animal), "access", new SubjectRef("user", user),
        new RequestContext(DateTimeOffset.UnixEpoch, new SubjectRef("user", user),
            new Dictionary<string, object?>()));

    // Common membership wiring: dr-smith is a vet and trained; jones is a vet but untrained.
    private static RelationTuple[] Members(string animal) =>
    [
        Tuple("animal", animal, "vet_member", new SubjectRef("group", "vets", "member")),
        Tuple("animal", animal, "trained_member", new SubjectRef("group", "trained", "member")),
        Tuple("group", "vets", "member", new SubjectRef("user", "dr-smith")),
        Tuple("group", "vets", "member", new SubjectRef("user", "jones")),
        Tuple("group", "trained", "member", new SubjectRef("user", "dr-smith")),
        Tuple("animal", animal, "can_access", new SubjectRef("user", "dr-smith")),
        Tuple("animal", animal, "can_access", new SubjectRef("user", "jones")),
    ];

    [Fact]
    public async Task Trained_vet_inside_quarantine_is_allowed()
    {
        var tuples = new List<RelationTuple>(Members("EL-001"))
        {
            Tuple("animal", "EL-001", "enclosure", new SubjectRef("enclosure", "Q1")),
            Tuple("enclosure", "Q1", "is_quarantine", new SubjectRef("user", "*")),
        };
        var auth = await NewAsync(Build(), tuples.ToArray());
        (await auth.CheckAsync(Access("EL-001", "dr-smith"))).Allowed.ShouldBeTrue();
    }

    [Fact]
    public async Task Untrained_vet_inside_quarantine_is_denied()
    {
        var tuples = new List<RelationTuple>(Members("EL-001"))
        {
            Tuple("animal", "EL-001", "enclosure", new SubjectRef("enclosure", "Q1")),
            Tuple("enclosure", "Q1", "is_quarantine", new SubjectRef("user", "*")),
        };
        var auth = await NewAsync(Build(), tuples.ToArray());
        // jones has can_access, but base is revoked inside quarantine and jones is untrained.
        (await auth.CheckAsync(Access("EL-001", "jones"))).Allowed.ShouldBeFalse();
    }

    [Fact]
    public async Task Outside_quarantine_base_access_passes_through()
    {
        // EL-002 is in a normal enclosure (no is_quarantine wildcard tuple).
        var tuples = new List<RelationTuple>(Members("EL-002"))
        {
            Tuple("animal", "EL-002", "enclosure", new SubjectRef("enclosure", "KH1")),
        };
        var auth = await NewAsync(Build(), tuples.ToArray());
        (await auth.CheckAsync(Access("EL-002", "jones"))).Allowed.ShouldBeTrue();   // base access intact
        (await auth.CheckAsync(Access("EL-002", "dr-smith"))).Allowed.ShouldBeTrue();
    }
}
