using Custodex.Abstractions;
using Custodex.Core.Conditions;
using Custodex.Core.Evaluation;
using Custodex.Storage.InMemory;
using Shouldly;

namespace Custodex.Core.Tests.Evaluation;

public class ListSubjectsTests
{
    private static readonly TenantContext T = new("zoo", "t1");

    private static Schema Build() => new SchemaBuilder("v1")
        .Type("group", t => t.Relation("member", s => s.User().SubjectSet("group", "member")))
        .Type("doc", t => t
            .Relation("viewer", s => s.User().SubjectSet("group", "member"))
            .Relation("blocked", s => s.User())
            .Permission("view", p => p.Relation("viewer").Exclude(x => x.Relation("blocked"))))
        .Build();

    private static async Task<EngineDrivenAuthorizer> NewAsync(params RelationTuple[] tuples)
    {
        var schemaStore = new InMemorySchemaStore();
        var relations = new InMemoryRelationStore();
        var attributes = new InMemoryAttributeStore();
        var uow = new NoOpUnitOfWork();
        await schemaStore.SetActiveAsync(T.Store, Build(), uow);
        await relations.WriteAsync(T, tuples, Array.Empty<RelationTuple>(), uow);
        await uow.CommitAsync();
        return new EngineDrivenAuthorizer(schemaStore, relations, attributes, new NullConditionEvaluator());
    }

    private static RelationTuple Tuple(string ot, string oid, string rel, SubjectRef s) =>
        new(new EntityRef(ot, oid), rel, s);

    private static ListSubjectsRequest Req(int pageSize = 100, string? token = null) => new(
        T, new EntityRef("doc", "D1"), "view",
        new RequestContext(DateTimeOffset.UnixEpoch, new SubjectRef("user", "system"),
            new Dictionary<string, object?>()), pageSize, token);

    [Fact]
    public async Task Lists_leaf_users_via_nested_groups_honouring_exclusion()
    {
        var auth = await NewAsync(
            Tuple("doc", "D1", "viewer", new SubjectRef("group", "staff", "member")),
            Tuple("group", "staff", "member", new SubjectRef("user", "alice")),
            Tuple("group", "staff", "member", new SubjectRef("user", "bob")),
            Tuple("doc", "D1", "blocked", new SubjectRef("user", "bob")));      // bob revoked

        var result = await auth.ListSubjectsAsync(Req());
        result.Subjects.Select(s => s.Id).ShouldBe(new[] { "alice" });
    }

    [Fact]
    public async Task Paginates_subjects_to_exact_page_size()
    {
        var auth = await NewAsync(
            Tuple("doc", "D1", "viewer", new SubjectRef("group", "staff", "member")),
            Tuple("group", "staff", "member", new SubjectRef("user", "a")),
            Tuple("group", "staff", "member", new SubjectRef("user", "b")),
            Tuple("group", "staff", "member", new SubjectRef("user", "c")));

        var page1 = await auth.ListSubjectsAsync(Req(pageSize: 2));
        page1.Subjects.Select(s => s.Id).ShouldBe(new[] { "a", "b" });
        page1.ContinuationToken.ShouldNotBeNull();

        var page2 = await auth.ListSubjectsAsync(Req(pageSize: 2, token: page1.ContinuationToken));
        page2.Subjects.Select(s => s.Id).ShouldBe(new[] { "c" });
        page2.ContinuationToken.ShouldBeNull();
    }

    [Fact]
    public async Task Wildcard_grant_surfaces_as_star_and_respects_page_size()
    {
        // A public grant (viewer@user:*) surfaces as the "*" subject and flows through
        // the normal confirm + paginate loop — it must NOT push the page over PageSize.
        var auth = await NewAsync(
            Tuple("doc", "D1", "viewer", new SubjectRef("user", "*")),
            Tuple("doc", "D1", "viewer", new SubjectRef("user", "a")),
            Tuple("doc", "D1", "viewer", new SubjectRef("user", "b")));

        var page1 = await auth.ListSubjectsAsync(Req(pageSize: 2));
        page1.Subjects.Count.ShouldBe(2);                       // exactly PageSize, no bonus row
        page1.Subjects.Select(s => s.Id).ShouldBe(new[] { "*", "a" });   // "*" sorts first ordinal
        page1.ContinuationToken.ShouldNotBeNull();

        var page2 = await auth.ListSubjectsAsync(Req(pageSize: 2, token: page1.ContinuationToken));
        page2.Subjects.Select(s => s.Id).ShouldBe(new[] { "b" });
        page2.ContinuationToken.ShouldBeNull();
    }

    [Fact]
    public async Task Cyclic_group_membership_does_not_hang_collection()
    {
        // group a <-> b membership cycle. Without a relation-level cycle guard in the
        // leaf-user collector, this would recurse unbounded (stack overflow / hang).
        var auth = await NewAsync(
            Tuple("doc", "D1", "viewer", new SubjectRef("group", "a", "member")),
            Tuple("group", "a", "member", new SubjectRef("group", "b", "member")),
            Tuple("group", "b", "member", new SubjectRef("group", "a", "member")),
            Tuple("group", "a", "member", new SubjectRef("user", "alice")));

        var result = await auth.ListSubjectsAsync(Req());
        result.Subjects.Select(s => s.Id).ShouldBe(new[] { "alice" });
    }
}
