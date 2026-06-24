using Custodex.Abstractions;
using Custodex.Core.Conditions;
using Custodex.Core.Evaluation;
using Custodex.Storage.InMemory;
using Shouldly;

namespace Custodex.Core.Tests.Conditions;

public class CelConditionEvaluatorTests
{
    private static readonly TenantContext T = new("zoo", "t1");

    private static ConditionDef IsCreatorDef() => new(
        "is_creator",
        [],
        new Compare(new AttributeRef("created_by"), CompareOp.Eq, new ContextSubject()));

    private static RequestContext Ctx(string subjectId) =>
        new(DateTimeOffset.UnixEpoch, new SubjectRef("user", subjectId), new Dictionary<string, object?>());

    [Fact]
    public void Adapter_forwards_to_static_evaluator_satisfied_and_denied()
    {
        var adapter = new CelConditionEvaluator();
        var def = IsCreatorDef();
        var inv = new ConditionRef("is_creator", new Dictionary<string, object?>());

        adapter.Evaluate(def, inv,
            new Dictionary<string, object?> { ["created_by"] = "dr-smith" },
            Ctx("dr-smith")).ShouldBeTrue();

        adapter.Evaluate(def, inv,
            new Dictionary<string, object?> { ["created_by"] = "alice" },
            Ctx("dr-smith")).ShouldBeFalse();
    }

    [Fact]
    public async Task End_to_end_conditioned_branch_gates_on_is_creator()
    {
        var schema = new SchemaBuilder("v1")
            .Type("doc", t => t
                .Relation("viewer", s => s.User())
                .Permission("view", p => p.Relation("viewer").Conditioned("is_creator")))
            .Condition("is_creator", p => { }, b => b.Eq(b.Attribute("created_by"), b.Subject()))
            .Build();

        var viewerTuple = new RelationTuple(new EntityRef("doc", "D1"), "viewer", new SubjectRef("user", "alice"));

        // Case 1: created_by == subject → Allowed
        {
            var schemaStore = new InMemorySchemaStore();
            var relations = new InMemoryRelationStore();
            var attributes = new InMemoryAttributeStore();
            var uow = new NoOpUnitOfWork();
            await schemaStore.SetActiveAsync(T.Store, schema, uow);
            await relations.WriteAsync(T, [viewerTuple], [], uow);
            await attributes.SetAsync(T, new EntityRef("doc", "D1"),
                new Dictionary<string, object?> { ["created_by"] = "alice" }, uow);
            await uow.CommitAsync();

            var auth = new EngineDrivenAuthorizer(schemaStore, relations, attributes, new CelConditionEvaluator());
            var req = new CheckRequest(T, new EntityRef("doc", "D1"), "view", new SubjectRef("user", "alice"),
                Ctx("alice"));
            (await auth.CheckAsync(req)).Allowed.ShouldBeTrue();
        }

        // Case 2: created_by != subject → Denied (condition gates the viewer grant)
        {
            var schemaStore = new InMemorySchemaStore();
            var relations = new InMemoryRelationStore();
            var attributes = new InMemoryAttributeStore();
            var uow = new NoOpUnitOfWork();
            await schemaStore.SetActiveAsync(T.Store, schema, uow);
            await relations.WriteAsync(T, [viewerTuple], [], uow);
            await attributes.SetAsync(T, new EntityRef("doc", "D1"),
                new Dictionary<string, object?> { ["created_by"] = "bob" }, uow);
            await uow.CommitAsync();

            var auth = new EngineDrivenAuthorizer(schemaStore, relations, attributes, new CelConditionEvaluator());
            var req = new CheckRequest(T, new EntityRef("doc", "D1"), "view", new SubjectRef("user", "alice"),
                Ctx("alice"));
            (await auth.CheckAsync(req)).Allowed.ShouldBeFalse();
        }
    }
}
