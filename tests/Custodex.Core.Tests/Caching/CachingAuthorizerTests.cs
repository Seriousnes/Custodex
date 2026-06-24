using System.Diagnostics.Metrics;
using Custodex.Abstractions;
using Custodex.Core.Caching;
using Custodex.Core.Conditions;
using Custodex.Core.Evaluation;
using Custodex.Storage.InMemory;
using Custodex.TestKit;
using Shouldly;

namespace Custodex.Core.Tests.Caching;

public class CachingAuthorizerTests
{
    private readonly TestWorld _world = TestWorld.New();
    private readonly string _objType;
    private readonly string _viewer;
    private readonly string _view;
    private readonly string _objId;
    private readonly string _condition;
    private readonly string _subjectId;

    public CachingAuthorizerTests()
    {
        _objType = _world.EntityType();
        _viewer = _world.Relation();
        _view = _world.Permission();
        _objId = _world.ObjectId();
        _condition = _world.ConditionName();
        _subjectId = _world.SubjectId();
    }

    private TenantContext T => _world.Tenant;

    private sealed class CountingRelationStore : IRelationStore
    {
        private readonly InMemoryRelationStore _inner;
        public int GetByObjectCalls { get; private set; }
        public CountingRelationStore(InMemoryRelationStore inner) => _inner = inner;

        public Task<IReadOnlyList<RelationTuple>> GetByObjectAsync(TenantContext t, EntityRef obj, string relation, CancellationToken ct = default)
        { GetByObjectCalls++; return _inner.GetByObjectAsync(t, obj, relation, ct); }
        public Task<IReadOnlyList<RelationTuple>> GetBySubjectAsync(TenantContext t, SubjectRef subject, CancellationToken ct = default)
            => _inner.GetBySubjectAsync(t, subject, ct);
        public Task WriteAsync(TenantContext t, IReadOnlyList<RelationTuple> add, IReadOnlyList<RelationTuple> remove, IUnitOfWork uow, CancellationToken ct = default)
            => _inner.WriteAsync(t, add, remove, uow, ct);
        public Task<IReadOnlyList<string>> ListObjectIdsAsync(TenantContext t, string objectType, CancellationToken ct = default)
            => _inner.ListObjectIdsAsync(t, objectType, ct);
    }

    private Schema UnconditionedSchema() => new SchemaBuilder(_world.Version)
        .Type(_objType, t => t
            .Relation(_viewer, s => s.Type(_world.UserType))
            .Permission(_view, p => p.Relation(_viewer)))
        .Build();

    private Schema ConditionedSchema() => new SchemaBuilder(_world.Version)
        .Type(_objType, t => t
            .Relation(_viewer, s => s.Type(_world.UserType))
            .Permission(_view, p => p.Relation(_viewer)))
        .Condition(_condition, c => { })
        .Build();

    private async Task<(CachingAuthorizer Auth, CountingRelationStore Counter, InMemoryCacheStore Cache, IUnitOfWork Uow)>
        NewAsync(Schema schema, IConditionEvaluator conditions, params RelationTuple[] tuples)
    {
        var schemaStore = new InMemorySchemaStore();
        var raw = new InMemoryRelationStore();
        var counter = new CountingRelationStore(raw);
        var attributes = new InMemoryAttributeStore();
        var cache = new InMemoryCacheStore();
        var uow = new NoOpUnitOfWork();
        await schemaStore.SetActiveAsync(T.Store, schema, uow);
        await raw.WriteAsync(T, tuples, Array.Empty<RelationTuple>(), uow);
        await uow.CommitAsync();
        var inner = new EngineDrivenAuthorizer(schemaStore, counter, attributes, conditions);
        return (new CachingAuthorizer(inner, schemaStore, cache), counter, cache, new NoOpUnitOfWork());
    }

    private CheckRequest Req(string user, bool explain = false) => new(
        T, new EntityRef(_objType, _objId), _view, new SubjectRef(_world.UserType, user),
        new RequestContext(DateTimeOffset.UnixEpoch, new SubjectRef(_world.UserType, user),
            new Dictionary<string, object?>()), Explain: explain);

    [Fact]
    public async Task Second_identical_check_is_served_from_cache()
    {
        var (auth, counter, _, _) = await NewAsync(UnconditionedSchema(), new NullConditionEvaluator(),
            new RelationTuple(new EntityRef(_objType, _objId), _viewer, _world.User(_subjectId)));

        (await auth.CheckAsync(Req(_subjectId))).Allowed.ShouldBeTrue();
        var afterFirst = counter.GetByObjectCalls;
        afterFirst.ShouldBeGreaterThan(0);

        (await auth.CheckAsync(Req(_subjectId))).Allowed.ShouldBeTrue();
        counter.GetByObjectCalls.ShouldBe(afterFirst);   // no further store reads: cache hit
    }

    [Fact]
    public async Task Epoch_bump_invalidates_the_cache()
    {
        var (auth, counter, cache, uow) = await NewAsync(UnconditionedSchema(), new NullConditionEvaluator(),
            new RelationTuple(new EntityRef(_objType, _objId), _viewer, _world.User(_subjectId)));

        await auth.CheckAsync(Req(_subjectId));
        var afterFirst = counter.GetByObjectCalls;

        await cache.BumpEpochAsync(T, uow);   // simulate a write bumping the epoch
        await uow.CommitAsync();

        await auth.CheckAsync(Req(_subjectId));
        counter.GetByObjectCalls.ShouldBeGreaterThan(afterFirst);   // epoch mismatch => miss => recompute
    }

    [Fact]
    public async Task Conditioned_results_are_never_cached()
    {
        // A schema whose tuple carries a condition => result touches a condition => not cacheable.
        var schema = ConditionedSchema();
        var conditioned = new RelationTuple(
            new EntityRef(_objType, _objId), _viewer, _world.User(_subjectId),
            new ConditionRef(_condition, new Dictionary<string, object?>()));
        var (auth, counter, _, _) = await NewAsync(schema, new AlwaysTrueConditionEvaluator(), conditioned);

        await auth.CheckAsync(Req(_subjectId));
        var afterFirst = counter.GetByObjectCalls;

        await auth.CheckAsync(Req(_subjectId));
        counter.GetByObjectCalls.ShouldBeGreaterThan(afterFirst);   // recomputed: condition touched => not cached
    }

    [Fact]
    public async Task Explain_requests_bypass_the_cache()
    {
        var (auth, counter, _, _) = await NewAsync(UnconditionedSchema(), new NullConditionEvaluator(),
            new RelationTuple(new EntityRef(_objType, _objId), _viewer, _world.User(_subjectId)));

        var explained = await auth.CheckAsync(Req(_subjectId, explain: true));
        explained.Explain.ShouldNotBeNull();
        var afterFirst = counter.GetByObjectCalls;

        var explainedAgain = await auth.CheckAsync(Req(_subjectId, explain: true));
        explainedAgain.Explain.ShouldNotBeNull();
        counter.GetByObjectCalls.ShouldBeGreaterThan(afterFirst);   // never cached: fresh trace each time
    }

    [Fact]
    public async Task Records_cache_hits_and_misses()
    {
        long hits = 0, misses = 0;
        using var ml = new MeterListener();
        ml.InstrumentPublished = (inst, l) =>
        {
            if (inst.Meter.Name == "Custodex" && inst.Name is "Custodex.cache.hits" or "Custodex.cache.misses")
                l.EnableMeasurementEvents(inst);
        };
        ml.SetMeasurementEventCallback<long>((inst, m, _, _) =>
        {
            if (inst.Name == "Custodex.cache.hits") hits += m;
            else if (inst.Name == "Custodex.cache.misses") misses += m;
        });
        ml.Start();

        var (auth, _, _, _) = await NewAsync(UnconditionedSchema(), new NullConditionEvaluator(),
            new RelationTuple(new EntityRef(_objType, _objId), _viewer, _world.User(_subjectId)));

        await auth.CheckAsync(Req(_subjectId));   // miss
        await auth.CheckAsync(Req(_subjectId));   // hit
        ml.Dispose();

        misses.ShouldBeGreaterThanOrEqualTo(1);
        hits.ShouldBeGreaterThanOrEqualTo(1);
    }

    private sealed class AlwaysTrueConditionEvaluator : IConditionEvaluator
    {
        public bool Evaluate(ConditionDef definition, ConditionRef invocation,
            IReadOnlyDictionary<string, object?> resourceAttributes, RequestContext context) => true;
    }
}
