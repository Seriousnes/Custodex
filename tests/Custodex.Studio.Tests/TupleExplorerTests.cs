using Bunit;

using Custodex.Abstractions;
using Custodex.Studio;
using Custodex.Studio.Components.Pages;
using Custodex.TestKit;

using Microsoft.Extensions.DependencyInjection;

using Shouldly;

namespace Custodex.Studio.Tests;

public sealed class TupleExplorerTests
{
    private sealed class FakeRelationManager : IRelationManager
    {
        private readonly IReadOnlyList<RelationTuple> _readResult;
        private readonly Exception? _readException;

        public FakeRelationManager(IReadOnlyList<RelationTuple>? readResult = null, Exception? readException = null)
        {
            _readResult = readResult ?? [];
            _readException = readException;
        }

        public int ReadCount { get; private set; }

        public TenantContext? CapturedWriteTenant { get; private set; }
        public string? CapturedWriteActor { get; private set; }
        public IReadOnlyList<RelationTuple>? CapturedWriteTuples { get; private set; }

        public TenantContext? CapturedDeleteTenant { get; private set; }
        public string? CapturedDeleteActor { get; private set; }
        public IReadOnlyList<RelationTuple>? CapturedDeleteTuples { get; private set; }

        public TenantContext? CapturedAttrsTenant { get; private set; }
        public string? CapturedAttrsActor { get; private set; }
        public EntityRef? CapturedAttrsObj { get; private set; }
        public IReadOnlyDictionary<string, object?>? CapturedAttrsDict { get; private set; }

        public Task<IReadOnlyList<RelationTuple>> ReadTuplesAsync(TenantContext tenant, TupleFilter filter, CancellationToken ct = default)
        {
            ReadCount++;
            return _readException is not null
                ? Task.FromException<IReadOnlyList<RelationTuple>>(_readException)
                : Task.FromResult(_readResult);
        }

        public Task<ConsistencyToken> WriteTuplesAsync(TenantContext tenant, string actor, IReadOnlyList<RelationTuple> tuples, CancellationToken ct = default)
        {
            CapturedWriteTenant = tenant;
            CapturedWriteActor = actor;
            CapturedWriteTuples = tuples;
            return Task.FromResult(ConsistencyToken.Create(tenant, 0, 0));
        }

        public Task<ConsistencyToken> DeleteTuplesAsync(TenantContext tenant, string actor, IReadOnlyList<RelationTuple> tuples, CancellationToken ct = default)
        {
            CapturedDeleteTenant = tenant;
            CapturedDeleteActor = actor;
            CapturedDeleteTuples = tuples;
            return Task.FromResult(ConsistencyToken.Create(tenant, 0, 0));
        }

        public Task<ConsistencyToken> WriteAttributesAsync(TenantContext tenant, string actor, EntityRef obj, IReadOnlyDictionary<string, object?> attributes, CancellationToken ct = default)
        {
            CapturedAttrsTenant = tenant;
            CapturedAttrsActor = actor;
            CapturedAttrsObj = obj;
            CapturedAttrsDict = attributes;
            return Task.FromResult(ConsistencyToken.Create(tenant, 0, 0));
        }

        public Task<IReadOnlyList<ChangeLogEntry>> ReadChangeLogAsync(TenantContext tenant, ChangeLogFilter filter, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ChangeLogEntry>>([]);
    }

    private static BunitContext CreateContext(StudioConnectionState state, FakeRelationManager manager)
    {
        var ctx = new BunitContext();
        ctx.Services.AddSingleton(state);
        ctx.Services.AddSingleton<IRelationManager>(manager);
        return ctx;
    }

    [Fact]
    public void Not_connected_shows_notice_and_no_query_button()
    {
        using var ctx = CreateContext(new StudioConnectionState(), new FakeRelationManager());

        var cut = ctx.Render<TupleExplorer>();

        cut.Markup.ShouldContain("Overview");
        cut.FindAll("#query").ShouldBeEmpty();
        cut.FindAll("#create").ShouldBeEmpty();
        cut.FindAll("#set-attrs").ShouldBeEmpty();
    }

    [Fact]
    public void Query_renders_grid_with_subject_relation_and_condition()
    {
        var world = TestWorld.New();

        var objType1 = world.EntityType();
        var objId1 = world.ObjectId();
        var rel1 = world.Relation();
        var subjType1 = world.EntityType();
        var subjId1 = world.SubjectId();
        var subjRel1 = world.Relation();

        var objType2 = world.EntityType();
        var objId2 = world.ObjectId();
        var rel2 = world.Relation();
        var subjType2 = world.EntityType();
        var subjId2 = world.SubjectId();
        var condName = world.ConditionName();

        var tupleWithSubjRelation = new RelationTuple(
            new EntityRef(objType1, objId1),
            rel1,
            new SubjectRef(subjType1, subjId1, subjRel1));

        var tupleWithCondition = new RelationTuple(
            new EntityRef(objType2, objId2),
            rel2,
            new SubjectRef(subjType2, subjId2),
            new ConditionRef(condName, new Dictionary<string, object?>()));

        var fake = new FakeRelationManager([tupleWithSubjRelation, tupleWithCondition]);
        var state = new StudioConnectionState();
        state.Connect(world.Tenant.Store, world.Tenant.Tenant);

        using var ctx = CreateContext(state, fake);
        var cut = ctx.Render<TupleExplorer>();

        cut.Find("#query").Click();

        cut.WaitForAssertion(() =>
        {
            cut.Markup.ShouldContain(tupleWithSubjRelation.Object.ToString());
            cut.Markup.ShouldContain(tupleWithSubjRelation.Relation);
            cut.Markup.ShouldContain(tupleWithSubjRelation.Subject.ToString());

            cut.Markup.ShouldContain(tupleWithCondition.Object.ToString());
            cut.Markup.ShouldContain(tupleWithCondition.Relation);
            cut.Markup.ShouldContain(tupleWithCondition.Subject.ToString());
            cut.Markup.ShouldContain(condName);
        });
    }

    [Fact]
    public void Create_tuple_captures_args_and_refreshes_grid()
    {
        var world = TestWorld.New();

        var objType = world.EntityType();
        var objId = world.ObjectId();
        var rel = world.Relation();
        var subjType = world.EntityType();
        var subjId = world.SubjectId();
        var condName = world.ConditionName();
        var actorId = world.SubjectId();

        var fake = new FakeRelationManager([]);
        var state = new StudioConnectionState();
        state.Connect(world.Tenant.Store, world.Tenant.Tenant);

        using var ctx = CreateContext(state, fake);
        var cut = ctx.Render<TupleExplorer>();

        cut.Find("#query").Click();
        cut.WaitForAssertion(() => fake.ReadCount.ShouldBe(1));

        cut.Find("#actor").Change(actorId);
        cut.Find("#create-obj-type").Change(objType);
        cut.Find("#create-obj-id").Change(objId);
        cut.Find("#create-relation").Change(rel);
        cut.Find("#create-subj-type").Change(subjType);
        cut.Find("#create-subj-id").Change(subjId);
        cut.Find("#create-cond-name").Change(condName);

        cut.Find("#create").Click();

        cut.WaitForAssertion(() =>
        {
            fake.CapturedWriteActor.ShouldBe(actorId);
            fake.CapturedWriteTuples.ShouldNotBeNull();
            fake.CapturedWriteTuples!.Count.ShouldBe(1);

            var written = fake.CapturedWriteTuples![0];
            written.Object.Type.ShouldBe(objType);
            written.Object.Id.ShouldBe(objId);
            written.Relation.ShouldBe(rel);
            written.Subject.Type.ShouldBe(subjType);
            written.Subject.Id.ShouldBe(subjId);
            written.Condition.ShouldNotBeNull();
            written.Condition!.Name.ShouldBe(condName);

            fake.ReadCount.ShouldBe(2);
        });
    }

    [Fact]
    public void Delete_row_captures_tuple_and_actor()
    {
        var world = TestWorld.New();

        var objType = world.EntityType();
        var objId = world.ObjectId();
        var rel = world.Relation();
        var subjType = world.EntityType();
        var subjId = world.SubjectId();
        var actorId = world.SubjectId();

        var tuple = new RelationTuple(
            new EntityRef(objType, objId),
            rel,
            new SubjectRef(subjType, subjId));

        var fake = new FakeRelationManager([tuple]);
        var state = new StudioConnectionState();
        state.Connect(world.Tenant.Store, world.Tenant.Tenant);

        using var ctx = CreateContext(state, fake);
        var cut = ctx.Render<TupleExplorer>();

        cut.Find("#actor").Change(actorId);
        cut.Find("#query").Click();

        cut.WaitForAssertion(() => cut.FindAll(".tuples-delete").Count.ShouldBe(1));

        cut.Find(".tuples-delete").Click();

        cut.WaitForAssertion(() =>
        {
            fake.CapturedDeleteActor.ShouldBe(actorId);
            fake.CapturedDeleteTuples.ShouldNotBeNull();
            fake.CapturedDeleteTuples!.Count.ShouldBe(1);

            var deleted = fake.CapturedDeleteTuples![0];
            deleted.Object.Type.ShouldBe(objType);
            deleted.Object.Id.ShouldBe(objId);
            deleted.Relation.ShouldBe(rel);
            deleted.Subject.Type.ShouldBe(subjType);
            deleted.Subject.Id.ShouldBe(subjId);
        });
    }

    [Fact]
    public void Set_attributes_captures_entity_dict_and_actor()
    {
        var world = TestWorld.New();

        var objType = world.EntityType();
        var objId = world.ObjectId();
        var key1 = world.ParamName();
        var val1 = world.ObjectId();
        var key2 = world.ParamName();
        var val2 = world.ObjectId();
        var actorId = world.SubjectId();

        var fake = new FakeRelationManager([]);
        var state = new StudioConnectionState();
        state.Connect(world.Tenant.Store, world.Tenant.Tenant);

        using var ctx = CreateContext(state, fake);
        var cut = ctx.Render<TupleExplorer>();

        cut.Find("#actor").Change(actorId);
        cut.Find("#attr-obj-type").Change(objType);
        cut.Find("#attr-obj-id").Change(objId);

        cut.Find("#add-attr-row").Click();
        cut.Find("#add-attr-row").Click();

        cut.WaitForAssertion(() => cut.FindAll(".tuples-attr-row").Count.ShouldBe(2));

        cut.FindAll(".tuples-attr-row input[placeholder='Key']")[0].Change(key1);
        cut.FindAll(".tuples-attr-row input[placeholder='Value']")[0].Change(val1);
        cut.FindAll(".tuples-attr-row input[placeholder='Key']")[1].Change(key2);
        cut.FindAll(".tuples-attr-row input[placeholder='Value']")[1].Change(val2);

        cut.Find("#set-attrs").Click();

        cut.WaitForAssertion(() =>
        {
            fake.CapturedAttrsActor.ShouldBe(actorId);
            fake.CapturedAttrsObj.ShouldNotBeNull();
            fake.CapturedAttrsObj!.Value.Type.ShouldBe(objType);
            fake.CapturedAttrsObj!.Value.Id.ShouldBe(objId);
            fake.CapturedAttrsDict.ShouldNotBeNull();
            fake.CapturedAttrsDict!.Count.ShouldBe(2);
            fake.CapturedAttrsDict![key1].ShouldBe(val1);
            fake.CapturedAttrsDict![key2].ShouldBe(val2);
        });
    }

    [Fact]
    public void Engine_error_on_query_shows_message_without_throwing()
    {
        var world = TestWorld.New();

        var typeName = world.EntityType();
        var exception = new UnknownTypeException(typeName);
        var fake = new FakeRelationManager(readException: exception);
        var state = new StudioConnectionState();
        state.Connect(world.Tenant.Store, world.Tenant.Tenant);

        using var ctx = CreateContext(state, fake);
        var cut = ctx.Render<TupleExplorer>();

        cut.Find("#query").Click();

        cut.WaitForAssertion(() =>
        {
            cut.Find(".tuples-error").ShouldNotBeNull();
            cut.Markup.ShouldContain(exception.Message);
        });
    }
}
