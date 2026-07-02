using Custodex.Abstractions;
using Custodex.Core;
using Custodex.Core.Caching;
using Custodex.Core.Conditions;
using Custodex.Core.Evaluation;
using Custodex.Storage.InMemory;
using Custodex.TestKit;

using Shouldly;

namespace Custodex.Core.Tests.Caching;

public class ReadYourWritesTests
{
    [Fact]
    public async Task Replaying_the_token_from_a_write_makes_the_next_cached_check_see_the_write()
    {
        var world = TestWorld.New();
        var objType = world.EntityType();
        var viewer = world.Relation();
        var view = world.Permission();
        var objId = world.ObjectId();
        var subjectId = world.SubjectId();
        var subject = world.User(subjectId);

        var schema = new SchemaBuilder(TestWorld.Version)
            .Type(objType, t => t
                .Relation(viewer, s => s.Type(world.UserType))
                .Permission(view, p => p.Relation(viewer)))
            .Build();

        var schemaStore = new InMemorySchemaStore();
        var relations = new InMemoryRelationStore();
        var attributes = new InMemoryAttributeStore();
        var changeLog = new InMemoryChangeLogStore();
        var cache = new InMemoryCacheStore();

        var setup = new NoOpUnitOfWork();
        await schemaStore.SetActiveAsync(world.Tenant.Store, schema, setup);
        await setup.CommitAsync();

        var engine = new EngineDrivenAuthorizer(schemaStore, relations, attributes, new NullConditionEvaluator());
        var caching = new CachingAuthorizer(engine, schemaStore, cache);

        CheckRequest Check(Consistency? consistency = null) => new(
            world.Tenant, new EntityRef(objType, objId), view, subject,
            new RequestContext(DateTimeOffset.UnixEpoch, subject, new Dictionary<string, object?>(), consistency));

        (await caching.CheckAsync(Check())).Allowed.ShouldBeFalse();

        var writeUow = new NoOpUnitOfWork();
        var tuple = new RelationTuple(new EntityRef(objType, objId), viewer, subject);
        await relations.WriteAsync(world.Tenant, [tuple], [], writeUow);
        var changeLogId = await changeLog.AppendAsync(
            world.Tenant, new ChangeLogEntry(0, subjectId, "write", objId, null, null, default), writeUow);
        var epoch = await cache.BumpEpochAsync(world.Tenant, writeUow);
        await writeUow.CommitAsync();
        var token = ConsistencyToken.Create(world.Tenant, epoch, changeLogId);

        var afterWrite = await caching.CheckAsync(Check(Consistency.AtLeastAsFresh(token)));

        afterWrite.Allowed.ShouldBeTrue();
    }
}
