using Custodex.Abstractions;
using Custodex.Core;
using Custodex.Core.Conditions;
using Custodex.Core.Evaluation;
using Custodex.Storage.InMemory;
using Shouldly;
using Xunit;

namespace Custodex.Core.Tests.Evaluation;

public class StructuralProbeTests
{
    private static readonly TenantContext T = new("s1", "t1");

    private static async Task<(EngineDrivenAuthorizer Auth, SchemaIndex Index)> NewAsync(
        Schema schema, params RelationTuple[] tuples)
    {
        var schemaStore = new InMemorySchemaStore();
        var relations = new InMemoryRelationStore();
        var attributes = new InMemoryAttributeStore();
        var uow = new NoOpUnitOfWork();
        await schemaStore.SetActiveAsync(T.Store, schema, uow);
        if (tuples.Length > 0) await relations.WriteAsync(T, tuples, [], uow);
        await uow.CommitAsync();
        var auth = new EngineDrivenAuthorizer(schemaStore, relations, attributes, new NullConditionEvaluator());
        return (auth, new SchemaIndex(schema));
    }

    private static RequestContext Ctx() =>
        new(DateTimeOffset.UnixEpoch, new SubjectRef("user", "<rebuild>"), new Dictionary<string, object?>());

    [Fact]
    public async Task Unconditioned_grant_is_granted_and_not_flagged()
    {
        var schema = new SchemaBuilder("v1").Type("doc", t => t
            .Relation("viewer", s => s.User()).Permission("view", p => p.Relation("viewer"))).Build();
        var (auth, index) = await NewAsync(schema,
            new RelationTuple(new EntityRef("doc", "D1"), "viewer", new SubjectRef("user", "alice")));

        var g = await auth.CheckStructuralForTest(index, T, new EntityRef("doc", "D1"), "view", new SubjectRef("user", "alice"), Ctx());
        g.Granted.ShouldBeTrue();
        g.Conditioned.ShouldBeFalse();
    }

    [Fact]
    public async Task Tuple_carrying_a_condition_is_granted_structurally_and_flagged()
    {
        var schema = new SchemaBuilder("v1")
            .Type("doc", t => t.Relation("viewer", s => s.User()).Permission("view", p => p.Relation("viewer")))
            .Condition("within_hours", c => c.Int("start").Int("end"))
            .Build();
        var conditioned = new RelationTuple(new EntityRef("doc", "D1"), "viewer", new SubjectRef("user", "alice"),
            new ConditionRef("within_hours", new Dictionary<string, object?> { ["start"] = 8, ["end"] = 18 }));
        var (auth, index) = await NewAsync(schema, conditioned);

        var g = await auth.CheckStructuralForTest(index, T, new EntityRef("doc", "D1"), "view", new SubjectRef("user", "alice"), Ctx());
        g.Granted.ShouldBeTrue();
        g.Conditioned.ShouldBeTrue();
    }

    [Fact]
    public async Task Absent_grant_is_not_granted()
    {
        var schema = new SchemaBuilder("v1").Type("doc", t => t
            .Relation("viewer", s => s.User()).Permission("view", p => p.Relation("viewer"))).Build();
        var (auth, index) = await NewAsync(schema);

        var g = await auth.CheckStructuralForTest(index, T, new EntityRef("doc", "D1"), "view", new SubjectRef("user", "ghost"), Ctx());
        g.Granted.ShouldBeFalse();
    }
}
