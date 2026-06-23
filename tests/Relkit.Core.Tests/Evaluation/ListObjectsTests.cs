using Relkit.Abstractions;
using Relkit.Core;
using Relkit.Core.Conditions;
using Relkit.Core.Evaluation;
using Relkit.Storage.InMemory;
using Shouldly;
using Xunit;

namespace Relkit.Core.Tests.Evaluation;

public class ListObjectsTests
{
    private static readonly TenantContext T = new("zoo", "t1");

    private static Schema Build() => new SchemaBuilder("v1")
        .Type("group", t => t.Relation("member", s => s.User().SubjectSet("group", "member")))
        .Type("species", t => t
            .Relation("editor", s => s.User().SubjectSet("group", "member").Wildcard("user"))
            .Relation("blocked", s => s.User())
            .Permission("edit", p => p.Relation("editor").Exclude(x => x.Relation("blocked"))))
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

    private static ListObjectsRequest Req(string user, int pageSize = 100, string? token = null) => new(
        T, new SubjectRef("user", user), "species", "edit",
        new RequestContext(DateTimeOffset.UnixEpoch, new SubjectRef("user", user),
            new Dictionary<string, object?>()), pageSize, token);

    [Fact]
    public async Task Lists_only_confirmed_objects_respecting_exclusion()
    {
        var auth = await NewAsync(
            Tuple("species", "kangaroo", "editor", new SubjectRef("group", "macropods", "member")),
            Tuple("species", "wallaby", "editor", new SubjectRef("group", "macropods", "member")),
            Tuple("species", "wallaby", "blocked", new SubjectRef("user", "alice")),     // alice revoked on wallaby
            Tuple("group", "macropods", "member", new SubjectRef("user", "alice")));

        var result = await auth.ListObjectsAsync(Req("alice"));
        result.ObjectIds.ShouldBe(new[] { "kangaroo" });   // wallaby excluded
        result.ContinuationToken.ShouldBeNull();
    }

    [Fact]
    public async Task Wildcard_grant_lists_every_object_of_the_type()
    {
        var auth = await NewAsync(
            Tuple("species", "kangaroo", "editor", new SubjectRef("user", "*")),
            Tuple("species", "wallaby", "editor", new SubjectRef("user", "*")),
            Tuple("species", "emu", "editor", new SubjectRef("user", "*")));

        var result = await auth.ListObjectsAsync(Req("anyone"));
        result.ObjectIds.ShouldBe(new[] { "emu", "kangaroo", "wallaby" });   // sorted, all three
    }

    [Fact]
    public async Task Paginates_to_exact_page_size_with_resumable_cursor()
    {
        var auth = await NewAsync(
            Tuple("species", "a", "editor", new SubjectRef("user", "*")),
            Tuple("species", "b", "editor", new SubjectRef("user", "*")),
            Tuple("species", "c", "editor", new SubjectRef("user", "*")),
            Tuple("species", "d", "editor", new SubjectRef("user", "*")),
            Tuple("species", "e", "editor", new SubjectRef("user", "*")));

        var page1 = await auth.ListObjectsAsync(Req("anyone", pageSize: 2));
        page1.ObjectIds.ShouldBe(new[] { "a", "b" });
        page1.ContinuationToken.ShouldNotBeNull();

        var page2 = await auth.ListObjectsAsync(Req("anyone", pageSize: 2, token: page1.ContinuationToken));
        page2.ObjectIds.ShouldBe(new[] { "c", "d" });
        page2.ContinuationToken.ShouldNotBeNull();

        var page3 = await auth.ListObjectsAsync(Req("anyone", pageSize: 2, token: page2.ContinuationToken));
        page3.ObjectIds.ShouldBe(new[] { "e" });
        page3.ContinuationToken.ShouldBeNull();   // true end of results
    }

    [Fact]
    public async Task Pages_do_not_overlap_or_drop_across_the_full_range()
    {
        var auth = await NewAsync(
            Tuple("species", "a", "editor", new SubjectRef("user", "*")),
            Tuple("species", "b", "editor", new SubjectRef("user", "*")),
            Tuple("species", "c", "editor", new SubjectRef("user", "*")));

        var all = new List<string>();
        string? token = null;
        do
        {
            var page = await auth.ListObjectsAsync(Req("anyone", pageSize: 2, token: token));
            all.AddRange(page.ObjectIds);
            token = page.ContinuationToken;
        } while (token is not null);

        all.ShouldBe(new[] { "a", "b", "c" });   // no dupes, no gaps
    }
}
