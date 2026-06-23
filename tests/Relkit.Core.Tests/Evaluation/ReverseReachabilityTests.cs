using Relkit.Abstractions;
using Relkit.Core;
using Relkit.Core.Conditions;
using Relkit.Core.Evaluation;
using Relkit.Storage.InMemory;
using Shouldly;
using Xunit;

namespace Relkit.Core.Tests.Evaluation;

public class ReverseReachabilityTests
{
    private static readonly TenantContext T = new("zoo", "t1");

    private static Schema Build() => new SchemaBuilder("v1")
        .Type("group", t => t.Relation("member", s => s.User().SubjectSet("group", "member")))
        .Type("species", t => t
            .Relation("editor", s => s.User().SubjectSet("group", "member"))
            .Permission("edit", p => p.Relation("editor")))
        .Build();

    private static async Task<(EngineDrivenAuthorizer Auth, InMemoryRelationStore Rel)> NewAsync(params RelationTuple[] tuples)
    {
        var schemaStore = new InMemorySchemaStore();
        var relations = new InMemoryRelationStore();
        var attributes = new InMemoryAttributeStore();
        var uow = new NoOpUnitOfWork();
        await schemaStore.SetActiveAsync(T.Store, Build(), uow);
        await relations.WriteAsync(T, tuples, Array.Empty<RelationTuple>(), uow);
        await uow.CommitAsync();
        return (new EngineDrivenAuthorizer(schemaStore, relations, attributes, new NullConditionEvaluator()), relations);
    }

    private static RelationTuple Tuple(string ot, string oid, string rel, SubjectRef s) =>
        new(new EntityRef(ot, oid), rel, s);

    [Fact]
    public async Task Gathers_objects_reachable_via_direct_and_nested_group_grants()
    {
        var (auth, _) = await NewAsync(
            Tuple("species", "kangaroo", "editor", new SubjectRef("group", "macropods", "member")),
            Tuple("species", "wallaby", "editor", new SubjectRef("group", "macropods", "member")),
            Tuple("species", "emu", "editor", new SubjectRef("user", "someone-else")),
            Tuple("group", "macropods", "member", new SubjectRef("user", "alice")));

        var candidates = await auth.CandidateObjectsForTest(T, new SubjectRef("user", "alice"), "species");
        candidates.Select(c => c.Id).ShouldBe(new[] { "kangaroo", "wallaby" });   // sorted, distinct, excludes emu
    }

    [Fact]
    public async Task Candidate_enumeration_is_cycle_safe()
    {
        var (auth, _) = await NewAsync(
            Tuple("species", "kangaroo", "editor", new SubjectRef("group", "a", "member")),
            Tuple("group", "a", "member", new SubjectRef("group", "b", "member")),
            Tuple("group", "b", "member", new SubjectRef("group", "a", "member")),
            Tuple("group", "a", "member", new SubjectRef("user", "alice")));

        var candidates = await auth.CandidateObjectsForTest(T, new SubjectRef("user", "alice"), "species");
        candidates.Select(c => c.Id).ShouldBe(new[] { "kangaroo" });
    }
}
