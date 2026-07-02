using Custodex.Abstractions;
using Custodex.Core.Caching;
using Custodex.Core.Conditions;
using Custodex.Storage.InMemory;
using Custodex.TestKit;

using Microsoft.Extensions.Time.Testing;

using Shouldly;

namespace Custodex.Core.Tests.Caching;

public class ScopedCachingAuthorizerTests
{
    private const string V1 = "v1";
    private const string V2 = "v2";

    private static Schema Unconditioned(string version = V1) => new(version, [], []);

    private static Schema Conditioned(string version = V1) =>
        new(version, [], [new ConditionDef("c", [], new LiteralBool(true))]);

    private sealed class Harness
    {
        public required CountingAuthorizer Authorizer { get; init; }
        public required InMemorySchemaStore SchemaStore { get; init; }
        public required InMemoryCacheStore CacheStore { get; init; }
        public required FakeTimeProvider Time { get; init; }
        public required CustodexCacheOptions Options { get; init; }
        public required TenantContext Tenant { get; init; }
        public required EntityRef Object { get; init; }
        public required string Permission { get; init; }
        public required SubjectRef Subject { get; init; }

        public ScopedCachingAuthorizer NewCache(ISchemaStore? schema = null, ICacheStore? cache = null) =>
            new(Authorizer, schema ?? SchemaStore, cache ?? CacheStore, Options, Time);

        public CheckRequest Request(DateTimeOffset? now = null, IReadOnlyDictionary<string, object?>? attributes = null, bool explain = false)
        {
            var context = new RequestContext(now ?? Time.GetUtcNow(), Subject, attributes ?? EmptyAttrs);
            return new CheckRequest(Tenant, Object, Permission, Subject, context, explain);
        }

        public CheckRequest Request(Consistency consistency)
        {
            var context = new RequestContext(Time.GetUtcNow(), Subject, EmptyAttrs, consistency);
            return new CheckRequest(Tenant, Object, Permission, Subject, context);
        }

        public async Task BumpEpochAsync()
        {
            var uow = new NoOpUnitOfWork();
            await CacheStore.BumpEpochAsync(Tenant, uow);
            await uow.CommitAsync();
        }

        public async Task SetSchemaAsync(Schema schema)
        {
            var uow = new NoOpUnitOfWork();
            await SchemaStore.SetActiveAsync(Tenant.Store, schema, uow);
            await uow.CommitAsync();
        }
    }

    private static async Task<Harness> BuildAsync(
        Schema schema,
        bool allowed = true,
        Action<CustodexCacheOptions>? configure = null,
        Func<CheckRequest, CheckResult>? decide = null)
    {
        var world = TestWorld.New();
        var options = new CustodexCacheOptions { EpochRefreshInterval = TimeSpan.Zero };
        configure?.Invoke(options);

        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var schemaStore = new InMemorySchemaStore();
        var cacheStore = new InMemoryCacheStore(time);

        var harness = new Harness
        {
            Authorizer = new CountingAuthorizer(decide ?? (_ => new CheckResult(allowed))),
            SchemaStore = schemaStore,
            CacheStore = cacheStore,
            Time = time,
            Options = options,
            Tenant = world.Tenant,
            Object = new EntityRef(world.EntityType(), world.ObjectId()),
            Permission = world.Permission(),
            Subject = world.User(world.SubjectId()),
        };

        await harness.SetSchemaAsync(schema);
        return harness;
    }

    private sealed record Manual(
        ScopedCachingAuthorizer Cache,
        CountingAuthorizer Authorizer,
        InMemoryCacheStore CacheStore,
        TenantContext Tenant,
        CheckRequest Request);

    private static async Task<Manual> BuildManualAsync(
        ControllableTimeProvider time,
        Schema schema,
        Action<CustodexCacheOptions> configure,
        Func<CheckRequest, CheckResult> decide)
    {
        var world = TestWorld.New();
        var options = new CustodexCacheOptions { EpochRefreshInterval = TimeSpan.Zero };
        configure(options);

        var schemaStore = new InMemorySchemaStore();
        var cacheStore = new InMemoryCacheStore(time);
        var uow = new NoOpUnitOfWork();
        await schemaStore.SetActiveAsync(world.Tenant.Store, schema, uow);
        await uow.CommitAsync();

        var authorizer = new CountingAuthorizer(decide);
        var cache = new ScopedCachingAuthorizer(authorizer, schemaStore, cacheStore, options, time);

        var subject = world.User(world.SubjectId());
        var obj = new EntityRef(world.EntityType(), world.ObjectId());
        var request = new CheckRequest(
            world.Tenant, obj, world.Permission(), subject, new RequestContext(time.GetUtcNow(), subject, EmptyAttrs));

        return new Manual(cache, authorizer, cacheStore, world.Tenant, request);
    }

    [Fact]
    public async Task Crit1_second_identical_direct_check_is_served_from_cache()
    {
        var h = await BuildAsync(Unconditioned());
        var cache = h.NewCache();

        (await cache.CheckAsync(h.Request())).Allowed.ShouldBeTrue();
        (await cache.CheckAsync(h.Request())).Allowed.ShouldBeTrue();

        h.Authorizer.CheckCalls.ShouldBe(1);
    }

    [Fact]
    public async Task Distinct_scopes_do_not_share_cached_decisions()
    {
        var h = await BuildAsync(Unconditioned());
        var scopeA = h.NewCache();
        var scopeB = h.NewCache();

        await scopeA.CheckAsync(h.Request());
        await scopeB.CheckAsync(h.Request());

        h.Authorizer.CheckCalls.ShouldBe(2);
    }

    [Fact]
    public async Task Crit3_epoch_bump_reinvokes_the_engine_and_serves_the_flipped_decision()
    {
        var allow = true;
        var h = await BuildAsync(Unconditioned(), decide: _ => new CheckResult(allow));
        var cache = h.NewCache();

        (await cache.CheckAsync(h.Request())).Allowed.ShouldBeTrue();
        h.Authorizer.CheckCalls.ShouldBe(1);

        allow = false;
        await h.BumpEpochAsync();

        var second = await cache.CheckAsync(h.Request());

        h.Authorizer.CheckCalls.ShouldBe(2);
        second.Allowed.ShouldBeFalse();
    }

    [Fact]
    public async Task Epoch_bump_is_masked_within_the_refresh_interval_and_observed_after_it()
    {
        var allow = true;
        var h = await BuildAsync(
            Unconditioned(),
            decide: _ => new CheckResult(allow),
            configure: o => o.EpochRefreshInterval = TimeSpan.FromSeconds(5));
        var cache = h.NewCache();

        (await cache.CheckAsync(h.Request())).Allowed.ShouldBeTrue();
        h.Authorizer.CheckCalls.ShouldBe(1);

        allow = false;
        await h.BumpEpochAsync();

        h.Time.Advance(TimeSpan.FromSeconds(2));
        (await cache.CheckAsync(h.Request())).Allowed.ShouldBeTrue();
        h.Authorizer.CheckCalls.ShouldBe(1);

        h.Time.Advance(TimeSpan.FromSeconds(4));
        var afterInterval = await cache.CheckAsync(h.Request());
        h.Authorizer.CheckCalls.ShouldBe(2);
        afterInterval.Allowed.ShouldBeFalse();
    }

    [Fact]
    public async Task Schema_version_change_invalidates_entries()
    {
        var h = await BuildAsync(Unconditioned(V1));
        var cache = h.NewCache();

        await cache.CheckAsync(h.Request());
        h.Authorizer.CheckCalls.ShouldBe(1);

        await h.SetSchemaAsync(Unconditioned(V2));

        await cache.CheckAsync(h.Request());
        h.Authorizer.CheckCalls.ShouldBe(2);
    }

    [Fact]
    public async Task Conditioned_skip_evaluates_every_check_live()
    {
        var h = await BuildAsync(Conditioned(), configure: o => o.Conditioned = ConditionedCaching.Skip);
        var cache = h.NewCache();

        await cache.CheckAsync(h.Request());
        await cache.CheckAsync(h.Request());

        h.Authorizer.CheckCalls.ShouldBe(2);
    }

    [Fact]
    public async Task Conditioned_context_in_key_reuses_only_when_context_matches()
    {
        var h = await BuildAsync(Conditioned(), configure: o => o.Conditioned = ConditionedCaching.ContextInKey);
        var cache = h.NewCache();

        var now = h.Time.GetUtcNow();
        await cache.CheckAsync(h.Request(now));
        await cache.CheckAsync(h.Request(now));
        h.Authorizer.CheckCalls.ShouldBe(1);

        await cache.CheckAsync(h.Request(now.AddMinutes(1)));
        h.Authorizer.CheckCalls.ShouldBe(2);
    }

    [Fact]
    public async Task Conditioned_context_in_key_varies_on_attributes()
    {
        var h = await BuildAsync(Conditioned(), configure: o => o.Conditioned = ConditionedCaching.ContextInKey);
        var cache = h.NewCache();
        var now = h.Time.GetUtcNow();

        var a = new Dictionary<string, object?>(StringComparer.Ordinal) { ["region"] = "east" };
        var b = new Dictionary<string, object?>(StringComparer.Ordinal) { ["region"] = "west" };

        await cache.CheckAsync(h.Request(now, a));
        await cache.CheckAsync(h.Request(now, a));
        h.Authorizer.CheckCalls.ShouldBe(1);

        await cache.CheckAsync(h.Request(now, b));
        h.Authorizer.CheckCalls.ShouldBe(2);
    }

    [Fact]
    public async Task Conditioned_skip_never_serves_a_conditional_from_cache()
    {
        var world = TestWorld.New();
        var condition = world.ConditionName();
        var key = world.ParamName();
        var conditional = new CheckResult(CheckDecision.Conditional, [new UnmetCondition(condition, [key])]);
        var h = await BuildAsync(Conditioned(),
            configure: o => o.Conditioned = ConditionedCaching.Skip,
            decide: _ => conditional);
        var cache = h.NewCache();

        var first = await cache.CheckAsync(h.Request());
        var second = await cache.CheckAsync(h.Request());

        first.Decision.ShouldBe(CheckDecision.Conditional);
        second.Decision.ShouldBe(CheckDecision.Conditional);
        h.Authorizer.CheckCalls.ShouldBe(2);
    }

    [Fact]
    public async Task Conditional_under_context_in_key_is_never_served_for_a_context_that_would_resolve_it()
    {
        var world = TestWorld.New();
        var condition = world.ConditionName();
        var key = world.ParamName();
        var h = await BuildAsync(Conditioned(),
            configure: o => o.Conditioned = ConditionedCaching.ContextInKey,
            decide: req => req.Context.Attributes.ContainsKey(key)
                ? new CheckResult(true)
                : new CheckResult(CheckDecision.Conditional, [new UnmetCondition(condition, [key])]));
        var cache = h.NewCache();
        var now = h.Time.GetUtcNow();

        var withoutKey = await cache.CheckAsync(h.Request(now));
        withoutKey.Decision.ShouldBe(CheckDecision.Conditional);
        h.Authorizer.CheckCalls.ShouldBe(1);

        var resolving = new Dictionary<string, object?>(StringComparer.Ordinal) { [key] = true };
        var withKey = await cache.CheckAsync(h.Request(now, resolving));
        withKey.Decision.ShouldBe(CheckDecision.Allow);
        h.Authorizer.CheckCalls.ShouldBe(2);

        var repeat = await cache.CheckAsync(h.Request(now));
        repeat.Decision.ShouldBe(CheckDecision.Conditional);
        repeat.UnmetConditions[0].MissingKeys.ShouldBe([key]);
        h.Authorizer.CheckCalls.ShouldBe(2);
    }

    [Fact]
    public async Task Ttl_expiry_forces_reevaluation()
    {
        var h = await BuildAsync(Unconditioned(), configure: o => o.Ttl = TimeSpan.FromMinutes(2));
        var cache = h.NewCache();

        await cache.CheckAsync(h.Request());
        h.Authorizer.CheckCalls.ShouldBe(1);

        h.Time.Advance(TimeSpan.FromMinutes(3));

        await cache.CheckAsync(h.Request());
        h.Authorizer.CheckCalls.ShouldBe(2);
    }

    [Fact]
    public async Task Ttl_of_zero_disables_the_cache_and_every_check_is_a_live_engine_call()
    {
        var h = await BuildAsync(Unconditioned(), configure: o => o.Ttl = TimeSpan.Zero);
        var cache = h.NewCache();

        var first = await cache.CheckAsync(h.Request());
        var second = await cache.CheckAsync(h.Request());

        first.Allowed.ShouldBeTrue();
        second.Allowed.ShouldBeTrue();
        h.Authorizer.CheckCalls.ShouldBe(2);
    }

    [Fact]
    public async Task Ttl_is_measured_monotonically_a_backward_wall_clock_does_not_extend_an_entry()
    {
        var time = new ControllableTimeProvider(DateTimeOffset.UnixEpoch);
        var m = await BuildManualAsync(time, Unconditioned(), o => o.Ttl = TimeSpan.FromMinutes(2), _ => new CheckResult(true));

        await m.Cache.CheckAsync(m.Request);
        m.Authorizer.CheckCalls.ShouldBe(1);

        time.SetUtcNow(DateTimeOffset.UnixEpoch.AddHours(-1));
        time.AdvanceTimestamp(TimeSpan.FromMinutes(3));

        await m.Cache.CheckAsync(m.Request);
        m.Authorizer.CheckCalls.ShouldBe(2);
    }

    [Fact]
    public async Task Epoch_refresh_is_measured_monotonically_a_backward_wall_clock_does_not_freeze_it()
    {
        var time = new ControllableTimeProvider(DateTimeOffset.UnixEpoch);
        var allow = true;
        var m = await BuildManualAsync(
            time,
            Unconditioned(),
            o =>
            {
                o.EpochRefreshInterval = TimeSpan.FromSeconds(5);
                o.Ttl = TimeSpan.FromMinutes(2);
            },
            _ => new CheckResult(allow));

        (await m.Cache.CheckAsync(m.Request)).Allowed.ShouldBeTrue();
        m.Authorizer.CheckCalls.ShouldBe(1);

        allow = false;
        var uow = new NoOpUnitOfWork();
        await m.CacheStore.BumpEpochAsync(m.Tenant, uow);
        await uow.CommitAsync();

        time.SetUtcNow(DateTimeOffset.UnixEpoch.AddHours(-1));
        time.AdvanceTimestamp(TimeSpan.FromSeconds(6));

        var afterRefresh = await m.Cache.CheckAsync(m.Request);
        m.Authorizer.CheckCalls.ShouldBe(2);
        afterRefresh.Allowed.ShouldBeFalse();
    }

    [Fact]
    public async Task Disabled_evaluates_every_check_live()
    {
        var h = await BuildAsync(Unconditioned(), configure: o => o.Enabled = false);
        var cache = h.NewCache();

        await cache.CheckAsync(h.Request());
        await cache.CheckAsync(h.Request());

        h.Authorizer.CheckCalls.ShouldBe(2);
    }

    [Fact]
    public async Task Concurrent_identical_checks_share_one_engine_call()
    {
        var gate = new TaskCompletionSource();
        var h = await BuildAsync(Unconditioned());
        h.Authorizer.BeforeReturn = _ => gate.Task;
        var cache = h.NewCache();

        var first = cache.CheckAsync(h.Request());
        var second = cache.CheckAsync(h.Request());
        gate.SetResult();
        await Task.WhenAll(first, second);

        h.Authorizer.CheckCalls.ShouldBe(1);
    }

    [Fact]
    public async Task Write_during_inflight_compute_is_detected_on_the_next_read()
    {
        var gate = new TaskCompletionSource();
        var allow = true;
        var h = await BuildAsync(Unconditioned(), decide: _ => new CheckResult(allow));
        h.Authorizer.BeforeReturn = _ => gate.Task;
        var cache = h.NewCache();

        var inflight = cache.CheckAsync(h.Request());
        await h.BumpEpochAsync();
        gate.SetResult();
        (await inflight).Allowed.ShouldBeTrue();
        h.Authorizer.CheckCalls.ShouldBe(1);

        allow = false;
        h.Authorizer.BeforeReturn = null;
        var afterWrite = await cache.CheckAsync(h.Request());

        h.Authorizer.CheckCalls.ShouldBe(2);
        afterWrite.Allowed.ShouldBeFalse();
    }

    [Fact]
    public async Task A_faulting_engine_call_is_not_pinned()
    {
        var h = await BuildAsync(Unconditioned());
        h.Authorizer.ThrowOn = call => call == 1 ? new InvalidOperationException("boom") : null;
        var cache = h.NewCache();

        await Should.ThrowAsync<InvalidOperationException>(() => cache.CheckAsync(h.Request()));

        var recovered = await cache.CheckAsync(h.Request());

        h.Authorizer.CheckCalls.ShouldBe(2);
        recovered.Allowed.ShouldBeTrue();
    }

    [Fact]
    public async Task Explain_requests_bypass_the_cache()
    {
        var h = await BuildAsync(Unconditioned());
        var cache = h.NewCache();

        await cache.CheckAsync(h.Request(explain: true));
        await cache.CheckAsync(h.Request(explain: true));

        h.Authorizer.CheckCalls.ShouldBe(2);
    }

    [Fact]
    public async Task Failsafe_without_stores_the_cache_is_disabled_and_every_check_is_live()
    {
        var h = await BuildAsync(Unconditioned());
        var cache = new ScopedCachingAuthorizer(h.Authorizer, schemaStore: null, cacheStore: null, h.Options, h.Time);

        await cache.CheckAsync(h.Request());
        await cache.CheckAsync(h.Request());

        h.Authorizer.CheckCalls.ShouldBe(2);
    }

    [Fact]
    public async Task No_active_schema_falls_back_to_a_live_check()
    {
        var h = await BuildAsync(Unconditioned());
        var emptySchemaStore = new InMemorySchemaStore();
        var cache = h.NewCache(schema: emptySchemaStore);

        await cache.CheckAsync(h.Request());
        await cache.CheckAsync(h.Request());

        h.Authorizer.CheckCalls.ShouldBe(2);
    }

    [Fact]
    public async Task Batch_and_list_operations_delegate_straight_to_the_inner_engine()
    {
        var h = await BuildAsync(Unconditioned());
        var cache = h.NewCache();

        await cache.ListObjectsAsync(new ListObjectsRequest(
            h.Tenant, h.Subject, h.Object.Type, h.Permission, new RequestContext(h.Time.GetUtcNow(), h.Subject, EmptyAttrs)));
        await cache.ListObjectsAsync(new ListObjectsRequest(
            h.Tenant, h.Subject, h.Object.Type, h.Permission, new RequestContext(h.Time.GetUtcNow(), h.Subject, EmptyAttrs)));

        h.Authorizer.ListCalls.ShouldBe(2);
        h.Authorizer.CheckCalls.ShouldBe(0);
    }

    [Fact]
    public async Task Invalidate_subject_drops_only_that_subjects_entries()
    {
        var h = await BuildAsync(Unconditioned());
        var cache = h.NewCache();
        var other = new SubjectRef(h.Subject.Type, h.Subject.Id + "-other");
        var otherRequest = new CheckRequest(
            h.Tenant, h.Object, h.Permission, other, new RequestContext(h.Time.GetUtcNow(), other, EmptyAttrs));

        await cache.CheckAsync(h.Request());
        await cache.CheckAsync(otherRequest);
        h.Authorizer.CheckCalls.ShouldBe(2);

        cache.InvalidateSubject(h.Subject);

        await cache.CheckAsync(h.Request());
        await cache.CheckAsync(otherRequest);
        h.Authorizer.CheckCalls.ShouldBe(3);
    }

    [Fact]
    public async Task Invalidate_object_drops_only_that_objects_entries()
    {
        var h = await BuildAsync(Unconditioned());
        var cache = h.NewCache();
        var otherObject = new EntityRef(h.Object.Type, h.Object.Id + "-other");
        var otherRequest = new CheckRequest(
            h.Tenant, otherObject, h.Permission, h.Subject, new RequestContext(h.Time.GetUtcNow(), h.Subject, EmptyAttrs));

        await cache.CheckAsync(h.Request());
        await cache.CheckAsync(otherRequest);
        h.Authorizer.CheckCalls.ShouldBe(2);

        cache.InvalidateObject(h.Object);

        await cache.CheckAsync(h.Request());
        await cache.CheckAsync(otherRequest);
        h.Authorizer.CheckCalls.ShouldBe(3);
    }

    [Fact]
    public async Task Clear_drops_every_entry()
    {
        var h = await BuildAsync(Unconditioned());
        var cache = h.NewCache();

        await cache.CheckAsync(h.Request());
        h.Authorizer.CheckCalls.ShouldBe(1);

        cache.Clear();

        await cache.CheckAsync(h.Request());
        h.Authorizer.CheckCalls.ShouldBe(2);
    }

    [Fact]
    public async Task MinimizeLatency_serves_the_second_check_from_the_scope()
    {
        var h = await BuildAsync(Unconditioned());
        var cache = h.NewCache();

        await cache.CheckAsync(h.Request(Consistency.MinimizeLatency));
        await cache.CheckAsync(h.Request(Consistency.MinimizeLatency));

        h.Authorizer.CheckCalls.ShouldBe(1);
    }

    [Fact]
    public async Task FullyConsistent_never_serves_from_the_scope()
    {
        var h = await BuildAsync(Unconditioned());
        var cache = h.NewCache();

        await cache.CheckAsync(h.Request());
        await cache.CheckAsync(h.Request(Consistency.FullyConsistent));

        h.Authorizer.CheckCalls.ShouldBe(2);
    }

    [Fact]
    public async Task AtLeastAsFresh_with_a_satisfied_token_serves_from_the_scope()
    {
        var h = await BuildAsync(Unconditioned());
        var cache = h.NewCache();

        await cache.CheckAsync(h.Request());
        var epoch = await h.CacheStore.GetEpochAsync(h.Tenant);
        var token = ConsistencyToken.Create(h.Tenant, epoch, changeLogId: 0);

        await cache.CheckAsync(h.Request(Consistency.AtLeastAsFresh(token)));

        h.Authorizer.CheckCalls.ShouldBe(1);
    }

    [Fact]
    public async Task AtLeastAsFresh_with_a_stale_snapshot_recomputes_and_refreshes()
    {
        var allow = true;
        var h = await BuildAsync(
            Unconditioned(),
            decide: _ => new CheckResult(allow),
            configure: o => o.EpochRefreshInterval = TimeSpan.FromSeconds(5));
        var cache = h.NewCache();

        (await cache.CheckAsync(h.Request())).Allowed.ShouldBeTrue();
        h.Authorizer.CheckCalls.ShouldBe(1);

        allow = false;
        await h.BumpEpochAsync();
        h.Time.Advance(TimeSpan.FromSeconds(2));

        var epoch = await h.CacheStore.GetEpochAsync(h.Tenant);
        var token = ConsistencyToken.Create(h.Tenant, epoch, changeLogId: 0);

        var result = await cache.CheckAsync(h.Request(Consistency.AtLeastAsFresh(token)));

        h.Authorizer.CheckCalls.ShouldBe(2);
        result.Allowed.ShouldBeFalse();
    }

    [Fact]
    public async Task AtLeastAsFresh_with_a_future_epoch_token_recomputes_without_looping()
    {
        var h = await BuildAsync(Unconditioned());
        var cache = h.NewCache();

        await cache.CheckAsync(h.Request());

        var aheadEpoch = await h.CacheStore.GetEpochAsync(h.Tenant) + 1;
        var token = ConsistencyToken.Create(h.Tenant, aheadEpoch, changeLogId: 0);

        await cache.CheckAsync(h.Request(Consistency.AtLeastAsFresh(token)));

        h.Authorizer.CheckCalls.ShouldBe(2);
    }

    [Fact]
    public async Task AtLeastAsFresh_with_a_garbage_token_throws()
    {
        var h = await BuildAsync(Unconditioned());
        var cache = h.NewCache();

        var garbage = Consistency.AtLeastAsFresh(new ConsistencyToken("not-a-token"));

        await Should.ThrowAsync<InvalidConsistencyTokenException>(() => cache.CheckAsync(h.Request(garbage)));
    }

    private static readonly IReadOnlyDictionary<string, object?> EmptyAttrs =
        new Dictionary<string, object?>(StringComparer.Ordinal);
}
