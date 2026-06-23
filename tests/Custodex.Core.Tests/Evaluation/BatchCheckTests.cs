using Custodex.Abstractions;
using Custodex.Core;
using Custodex.Core.Conditions;
using Custodex.Core.Evaluation;
using Custodex.Storage.InMemory;
using Shouldly;
using Xunit;

namespace Custodex.Core.Tests.Evaluation;

public class BatchCheckTests
{
    private static readonly TenantContext T = new("zoo", "t1");

    private static async Task<EngineDrivenAuthorizer> NewAsync()
    {
        var schema = new SchemaBuilder("v1")
            .Type("group", t => t.Relation("member", s => s.User().SubjectSet("group", "member")))
            .Type("doc", t => t
                .Relation("viewer", s => s.User().SubjectSet("group", "member"))
                .Permission("view", p => p.Relation("viewer")))
            .Build();
        var schemaStore = new InMemorySchemaStore();
        var relations = new InMemoryRelationStore();
        var attributes = new InMemoryAttributeStore();
        var uow = new NoOpUnitOfWork();
        await schemaStore.SetActiveAsync(T.Store, schema, uow);
        await relations.WriteAsync(T, new[]
        {
            new RelationTuple(new EntityRef("doc", "D1"), "viewer", new SubjectRef("group", "vets", "member")),
            new RelationTuple(new EntityRef("doc", "D2"), "viewer", new SubjectRef("user", "bob")),
            new RelationTuple(new EntityRef("group", "vets"), "member", new SubjectRef("user", "alice")),
        }, Array.Empty<RelationTuple>(), uow);
        await uow.CommitAsync();
        return new EngineDrivenAuthorizer(schemaStore, relations, attributes, new NullConditionEvaluator());
    }

    [Fact]
    public async Task Batch_returns_a_result_per_item_in_order()
    {
        var auth = await NewAsync();
        var ctx = new RequestContext(DateTimeOffset.UnixEpoch, new SubjectRef("user", "system"),
            new Dictionary<string, object?>());
        var req = new BatchCheckRequest(T, new[]
        {
            new CheckItem(new EntityRef("doc", "D1"), "view", new SubjectRef("user", "alice")),  // via group => true
            new CheckItem(new EntityRef("doc", "D1"), "view", new SubjectRef("user", "bob")),    // false
            new CheckItem(new EntityRef("doc", "D2"), "view", new SubjectRef("user", "bob")),    // direct => true
        }, ctx);

        var results = await auth.BatchCheckAsync(req);
        results.Count.ShouldBe(3);
        results[0].Allowed.ShouldBeTrue();
        results[1].Allowed.ShouldBeFalse();
        results[2].Allowed.ShouldBeTrue();
    }

    [Fact]
    public async Task Empty_batch_returns_empty_list()
    {
        var auth = await NewAsync();
        var ctx = new RequestContext(DateTimeOffset.UnixEpoch, new SubjectRef("user", "system"),
            new Dictionary<string, object?>());
        var results = await auth.BatchCheckAsync(new BatchCheckRequest(T, Array.Empty<CheckItem>(), ctx));
        results.ShouldBeEmpty();
    }
}
