using Relkit.Abstractions;
using Relkit.Core;
using Relkit.Core.Conditions;
using Relkit.Core.Evaluation;
using Relkit.Storage.InMemory;
using Shouldly;
using Xunit;

namespace Relkit.Core.Tests.Evaluation;

public class AlgebraTests
{
    private static readonly TenantContext T = new("zoo", "t1");

    private static async Task<EngineDrivenAuthorizer> NewAsync(Schema schema, params RelationTuple[] tuples)
    {
        var schemaStore = new InMemorySchemaStore();
        var relations = new InMemoryRelationStore();
        var attributes = new InMemoryAttributeStore();
        var uow = new NoOpUnitOfWork();
        await schemaStore.SetActiveAsync(T.Store, schema, uow);
        if (tuples.Length > 0) await relations.WriteAsync(T, tuples, Array.Empty<RelationTuple>(), uow);
        await uow.CommitAsync();
        return new EngineDrivenAuthorizer(schemaStore, relations, attributes, new NullConditionEvaluator());
    }

    private static CheckRequest Req(EntityRef obj, string perm, string subjectId) => new(
        T, obj, perm, new SubjectRef("user", subjectId),
        new RequestContext(DateTimeOffset.UnixEpoch, new SubjectRef("user", subjectId),
            new Dictionary<string, object?>()));

    private static RelationTuple Tuple(string objType, string objId, string rel, SubjectRef subject) =>
        new(new EntityRef(objType, objId), rel, subject);

    [Fact]
    public async Task Union_grants_if_either_branch_holds()
    {
        var schema = new SchemaBuilder("v1")
            .Type("doc", t => t
                .Relation("viewer", s => s.User())
                .Relation("editor", s => s.User())
                .Permission("access", p => p.Relation("viewer").Union(x => x.Relation("editor"))))
            .Build();
        var auth = await NewAsync(schema, Tuple("doc", "D1", "editor", new SubjectRef("user", "alice")));
        (await auth.CheckAsync(Req(new EntityRef("doc", "D1"), "access", "alice"))).Allowed.ShouldBeTrue();
        (await auth.CheckAsync(Req(new EntityRef("doc", "D1"), "access", "bob"))).Allowed.ShouldBeFalse();
    }

    [Fact]
    public async Task Intersect_requires_both_branches()
    {
        var schema = new SchemaBuilder("v1")
            .Type("doc", t => t
                .Relation("vet", s => s.User())
                .Relation("trained", s => s.User())
                .Permission("access", p => p.Relation("vet").Intersect(x => x.Relation("trained"))))
            .Build();
        var auth = await NewAsync(schema,
            Tuple("doc", "D1", "vet", new SubjectRef("user", "alice")),
            Tuple("doc", "D1", "trained", new SubjectRef("user", "alice")),
            Tuple("doc", "D1", "vet", new SubjectRef("user", "bob")));
        (await auth.CheckAsync(Req(new EntityRef("doc", "D1"), "access", "alice"))).Allowed.ShouldBeTrue();
        (await auth.CheckAsync(Req(new EntityRef("doc", "D1"), "access", "bob"))).Allowed.ShouldBeFalse();
    }

    [Fact]
    public async Task Exclude_revokes_the_right_branch()
    {
        var schema = new SchemaBuilder("v1")
            .Type("doc", t => t
                .Relation("viewer", s => s.User())
                .Relation("blocked", s => s.User())
                .Permission("access", p => p.Relation("viewer").Exclude(x => x.Relation("blocked"))))
            .Build();
        var auth = await NewAsync(schema,
            Tuple("doc", "D1", "viewer", new SubjectRef("user", "alice")),
            Tuple("doc", "D1", "viewer", new SubjectRef("user", "carol")),
            Tuple("doc", "D1", "blocked", new SubjectRef("user", "carol")));
        (await auth.CheckAsync(Req(new EntityRef("doc", "D1"), "access", "alice"))).Allowed.ShouldBeTrue();
        (await auth.CheckAsync(Req(new EntityRef("doc", "D1"), "access", "carol"))).Allowed.ShouldBeFalse();
    }

    [Fact]
    public async Task Exclude_self_is_always_deny()
    {
        // a - a == deny for everyone.
        var schema = new SchemaBuilder("v1")
            .Type("doc", t => t
                .Relation("viewer", s => s.User())
                .Permission("access", p => p.Relation("viewer").Exclude(x => x.Relation("viewer"))))
            .Build();
        var auth = await NewAsync(schema, Tuple("doc", "D1", "viewer", new SubjectRef("user", "alice")));
        (await auth.CheckAsync(Req(new EntityRef("doc", "D1"), "access", "alice"))).Allowed.ShouldBeFalse();
    }

    [Fact]
    public async Task Arrow_inherits_through_a_related_object_permission()
    {
        // animal.edit = enclosure->edit ; enclosure.edit = editor
        var schema = new SchemaBuilder("v1")
            .Type("enclosure", t => t
                .Relation("editor", s => s.User())
                .Permission("edit", p => p.Relation("editor")))
            .Type("animal", t => t
                .Relation("enclosure", s => s.Type("enclosure"))
                .Permission("edit", p => p.Arrow("enclosure", "edit")))
            .Build();
        var auth = await NewAsync(schema,
            Tuple("animal", "EL-001", "enclosure", new SubjectRef("enclosure", "KH1")),
            Tuple("enclosure", "KH1", "editor", new SubjectRef("user", "alice")));
        (await auth.CheckAsync(Req(new EntityRef("animal", "EL-001"), "edit", "alice"))).Allowed.ShouldBeTrue();
        (await auth.CheckAsync(Req(new EntityRef("animal", "EL-001"), "edit", "bob"))).Allowed.ShouldBeFalse();
    }

    [Fact]
    public async Task Arrow_sees_inner_exclusion_on_the_related_object()
    {
        // The landmine: animal.edit -> enclosure.edit, and enclosure.edit contains - blocked.
        // A top-level post-filter could not see the inner exclusion; pointwise arrow recursion does.
        var schema = new SchemaBuilder("v1")
            .Type("enclosure", t => t
                .Relation("editor", s => s.User())
                .Relation("blocked", s => s.User())
                .Permission("edit", p => p.Relation("editor").Exclude(x => x.Relation("blocked"))))
            .Type("animal", t => t
                .Relation("enclosure", s => s.Type("enclosure"))
                .Permission("edit", p => p.Arrow("enclosure", "edit")))
            .Build();
        var auth = await NewAsync(schema,
            Tuple("animal", "EL-001", "enclosure", new SubjectRef("enclosure", "KH1")),
            Tuple("enclosure", "KH1", "editor", new SubjectRef("user", "carol")),
            Tuple("enclosure", "KH1", "blocked", new SubjectRef("user", "carol")));
        (await auth.CheckAsync(Req(new EntityRef("animal", "EL-001"), "edit", "carol"))).Allowed.ShouldBeFalse();
    }

    [Fact]
    public async Task Arrow_falls_back_to_a_relation_when_target_is_not_a_permission()
    {
        // enclosure->is_quarantine where is_quarantine is a relation backing the gate.
        var schema = new SchemaBuilder("v1")
            .Type("enclosure", t => t
                .Relation("is_quarantine", s => s.Wildcard("user"))
                .Permission("is_quarantine", p => p.Relation("is_quarantine")))
            .Type("animal", t => t
                .Relation("enclosure", s => s.Type("enclosure"))
                .Permission("quarantined", p => p.Arrow("enclosure", "is_quarantine")))
            .Build();
        var auth = await NewAsync(schema,
            Tuple("animal", "EL-001", "enclosure", new SubjectRef("enclosure", "Q1")),
            Tuple("enclosure", "Q1", "is_quarantine", new SubjectRef("user", "*")));
        (await auth.CheckAsync(Req(new EntityRef("animal", "EL-001"), "quarantined", "anyone"))).Allowed.ShouldBeTrue();
    }

    [Fact]
    public async Task Arrow_relation_fallback_resolves_when_target_has_no_such_permission()
    {
        // enclosure has a relation 'gate' but NO permission named 'gate'.
        // animal.guarded = enclosure->gate must fall back to resolving the 'gate' relation on the enclosure.
        var schema = new SchemaBuilder("v1")
            .Type("enclosure", t => t
                .Relation("gate", s => s.Wildcard("user")))
            .Type("animal", t => t
                .Relation("enclosure", s => s.Type("enclosure"))
                .Permission("guarded", p => p.Arrow("enclosure", "gate")))
            .Build();
        var auth = await NewAsync(schema,
            Tuple("animal", "EL-001", "enclosure", new SubjectRef("enclosure", "Q1")),
            Tuple("enclosure", "Q1", "gate", new SubjectRef("user", "*")));
        (await auth.CheckAsync(Req(new EntityRef("animal", "EL-001"), "guarded", "anyone"))).Allowed.ShouldBeTrue();
    }

    [Fact]
    public async Task Conditioned_branch_passes_through_when_condition_is_satisfied()
    {
        var schema = new SchemaBuilder("v1")
            .Type("doc", t => t
                .Relation("viewer", s => s.User())
                .Permission("view", p => p.Relation("viewer").Conditioned("always")))
            .Condition("always", c => { })
            .Build();
        var auth = await NewAsync(schema, Tuple("doc", "D1", "viewer", new SubjectRef("user", "alice")));
        // NullConditionEvaluator treats 'always' as satisfied, so alice (a viewer) is allowed; bob is not.
        (await auth.CheckAsync(Req(new EntityRef("doc", "D1"), "view", "alice"))).Allowed.ShouldBeTrue();
        (await auth.CheckAsync(Req(new EntityRef("doc", "D1"), "view", "bob"))).Allowed.ShouldBeFalse();
    }
}
