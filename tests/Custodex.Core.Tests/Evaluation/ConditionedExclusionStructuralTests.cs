using Custodex.Abstractions;
using Custodex.Core.Conditions;
using Custodex.Core.Evaluation;
using Custodex.Storage.InMemory;

using Shouldly;

namespace Custodex.Core.Tests.Evaluation;

public class ConditionedExclusionStructuralTests
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
    public async Task Conditioned_exclusion_right_is_granted_structurally_and_flagged_conditioned()
    {
        var schema = new SchemaBuilder("v1")
            .Type("doc", t => t
                .Relation("allow", s => s.User())
                .Relation("blocked", s => s.User())
                .Permission("access", p => p.Relation("allow").Exclude(x => x.Relation("blocked"))))
            .Condition("block", _ => { }, x => x.Const(false))
            .Build();

        var allow = new RelationTuple(new EntityRef("doc", "D1"), "allow", new SubjectRef("user", "alice"));
        var blockedConditioned = new RelationTuple(
            new EntityRef("doc", "D1"), "blocked", new SubjectRef("user", "alice"),
            new ConditionRef("block", new Dictionary<string, object?>()));
        var (auth, index) = await NewAsync(schema, allow, blockedConditioned);

        var g = await auth.CheckStructuralForTest(
            index, T, new EntityRef("doc", "D1"), "access", new SubjectRef("user", "alice"), Ctx());

        g.Granted.ShouldBeTrue();
        g.Conditioned.ShouldBeTrue();
    }

    [Fact]
    public async Task Unconditional_exclusion_right_is_conservatively_included_and_flagged_when_schema_has_conditions()
    {
        var schema = new SchemaBuilder("v1")
            .Type("doc", t => t
                .Relation("allow", s => s.User())
                .Relation("blocked", s => s.User())
                .Permission("access", p => p.Relation("allow").Exclude(x => x.Relation("blocked"))))
            .Condition("block", _ => { }, x => x.Const(false))
            .Build();

        var allow = new RelationTuple(new EntityRef("doc", "D1"), "allow", new SubjectRef("user", "alice"));
        var blocked = new RelationTuple(new EntityRef("doc", "D1"), "blocked", new SubjectRef("user", "alice"));
        var (auth, index) = await NewAsync(schema, allow, blocked);

        var g = await auth.CheckStructuralForTest(
            index, T, new EntityRef("doc", "D1"), "access", new SubjectRef("user", "alice"), Ctx());

        g.Granted.ShouldBeTrue();
        g.Conditioned.ShouldBeTrue();
    }
}
