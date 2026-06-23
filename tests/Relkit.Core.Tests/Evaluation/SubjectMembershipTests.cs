using Relkit.Abstractions;
using Relkit.Core;
using Relkit.Core.Conditions;
using Relkit.Core.Evaluation;
using Relkit.Storage.InMemory;
using Shouldly;
using Xunit;

namespace Relkit.Core.Tests.Evaluation;

public class SubjectMembershipTests
{
    private static readonly TenantContext T = new("zoo", "t1");

    private static Schema GroupSchema() => new SchemaBuilder("v1")
        .Type("group", t => t.Relation("member", s => s.User().SubjectSet("group", "member")))
        .Type("doc", t => t
            .Relation("viewer", s => s.User().SubjectSet("group", "member").Wildcard("user"))
            .Permission("view", p => p.Relation("viewer")))
        .Build();

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

    private static CheckRequest Req(string subjectId, string perm = "view") => new(
        T, new EntityRef("doc", "D1"), perm, new SubjectRef("user", subjectId),
        new RequestContext(DateTimeOffset.UnixEpoch, new SubjectRef("user", subjectId),
            new Dictionary<string, object?>()));

    [Fact]
    public async Task Direct_user_grant_matches()
    {
        var auth = await NewAsync(GroupSchema(),
            new RelationTuple(new EntityRef("doc", "D1"), "viewer", new SubjectRef("user", "alice")));
        (await auth.CheckAsync(Req("alice"))).Allowed.ShouldBeTrue();
        (await auth.CheckAsync(Req("bob"))).Allowed.ShouldBeFalse();
    }

    [Fact]
    public async Task Wildcard_grant_matches_everyone()
    {
        var auth = await NewAsync(GroupSchema(),
            new RelationTuple(new EntityRef("doc", "D1"), "viewer", new SubjectRef("user", "*")));
        (await auth.CheckAsync(Req("anyone"))).Allowed.ShouldBeTrue();
    }

    [Fact]
    public async Task Subject_set_grant_matches_via_group_membership()
    {
        var auth = await NewAsync(GroupSchema(),
            new RelationTuple(new EntityRef("doc", "D1"), "viewer", new SubjectRef("group", "vets", "member")),
            new RelationTuple(new EntityRef("group", "vets"), "member", new SubjectRef("user", "dr-smith")));
        (await auth.CheckAsync(Req("dr-smith"))).Allowed.ShouldBeTrue();
        (await auth.CheckAsync(Req("outsider"))).Allowed.ShouldBeFalse();
    }

    [Fact]
    public async Task Nested_group_membership_resolves_transitively()
    {
        var auth = await NewAsync(GroupSchema(),
            new RelationTuple(new EntityRef("doc", "D1"), "viewer", new SubjectRef("group", "staff", "member")),
            new RelationTuple(new EntityRef("group", "staff"), "member", new SubjectRef("group", "vets", "member")),
            new RelationTuple(new EntityRef("group", "vets"), "member", new SubjectRef("user", "dr-smith")));
        (await auth.CheckAsync(Req("dr-smith"))).Allowed.ShouldBeTrue();
    }

    [Fact]
    public async Task Group_membership_cycle_prunes_to_deny_without_throwing()
    {
        var auth = await NewAsync(GroupSchema(),
            new RelationTuple(new EntityRef("doc", "D1"), "viewer", new SubjectRef("group", "a", "member")),
            new RelationTuple(new EntityRef("group", "a"), "member", new SubjectRef("group", "b", "member")),
            new RelationTuple(new EntityRef("group", "b"), "member", new SubjectRef("group", "a", "member")));
        (await auth.CheckAsync(Req("ghost"))).Allowed.ShouldBeFalse();
    }

    [Fact]
    public async Task Permission_backed_by_a_same_named_relation_resolves_to_allow()
    {
        var schema = new SchemaBuilder("v1")
            .Type("doc", t => t
                .Relation("view", s => s.User())
                .Permission("view", p => p.Relation("view")))
            .Build();
        var auth = await NewAsync(schema,
            new RelationTuple(new EntityRef("doc", "D1"), "view", new SubjectRef("user", "alice")));
        (await auth.CheckAsync(Req("alice", "view"))).Allowed.ShouldBeTrue();
        (await auth.CheckAsync(Req("bob", "view"))).Allowed.ShouldBeFalse();
    }
}
