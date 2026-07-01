using Custodex.Abstractions;
using Custodex.Core.Conditions;
using Custodex.Storage.InMemory;
using Custodex.TestKit;

using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

using Shouldly;

namespace Custodex.AspNetCore.Tests.Caching;

public class CustodexDecisionCacheTests
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
        public required CustodexAuthorizationOptions Options { get; init; }
        public required TenantContext Tenant { get; init; }
        public required EntityRef Object { get; init; }
        public required string Permission { get; init; }
        public required SubjectRef Subject { get; init; }

        public CustodexDecisionCache NewCache(ISchemaStore? schema = null, ICacheStore? cache = null) =>
            new(Authorizer, schema ?? SchemaStore, cache ?? CacheStore, Microsoft.Extensions.Options.Options.Create(Options), Time);

        public CheckRequest Request(DateTimeOffset? now = null, IReadOnlyDictionary<string, object?>? attributes = null, bool explain = false)
        {
            var context = new RequestContext(now ?? Time.GetUtcNow(), Subject, attributes ?? EmptyAttributes);
            return new CheckRequest(Tenant, Object, Permission, Subject, context, explain);
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

        private static readonly IReadOnlyDictionary<string, object?> EmptyAttributes =
            new Dictionary<string, object?>(StringComparer.Ordinal);
    }

    private static async Task<Harness> BuildAsync(
        Schema schema,
        bool allowed = true,
        Action<DecisionCacheOptions>? configure = null,
        Func<CheckRequest, CheckResult>? decide = null)
    {
        var world = TestWorld.New();
        var options = new CustodexAuthorizationOptions { SubjectType = world.UserType };
        options.DecisionCache.EpochRefreshInterval = TimeSpan.Zero;
        configure?.Invoke(options.DecisionCache);

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

    [Fact]
    public async Task Crit1_second_identical_check_is_served_from_cache()
    {
        var h = await BuildAsync(Unconditioned());
        var cache = h.NewCache();

        (await cache.CheckAsync(h.Request())).Allowed.ShouldBeTrue();
        (await cache.CheckAsync(h.Request())).Allowed.ShouldBeTrue();

        h.Authorizer.CheckCalls.ShouldBe(1);
    }

    [Fact]
    public async Task Crit2_distinct_scopes_do_not_share_cached_decisions()
    {
        var h = await BuildAsync(Unconditioned());
        var scopeA = h.NewCache();
        var scopeB = h.NewCache();

        await scopeA.CheckAsync(h.Request());
        await scopeB.CheckAsync(h.Request());

        h.Authorizer.CheckCalls.ShouldBe(2);
    }

    [Fact]
    public async Task Crit3_epoch_bump_reinvokes_the_engine_and_serves_the_new_decision()
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
    public async Task Crit3b_epoch_bump_is_masked_within_the_refresh_interval_and_observed_after_it()
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
    public async Task Crit4_schema_version_change_invalidates_entries()
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
    public async Task Crit5_conditioned_skip_evaluates_every_check_live()
    {
        var h = await BuildAsync(Conditioned(), configure: o => o.Conditioned = ConditionedCaching.Skip);
        var cache = h.NewCache();

        await cache.CheckAsync(h.Request());
        await cache.CheckAsync(h.Request());

        h.Authorizer.CheckCalls.ShouldBe(2);
    }

    [Fact]
    public async Task Crit5_conditioned_context_in_key_reuses_only_when_context_matches()
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
    public async Task Crit5_conditioned_context_in_key_varies_on_attributes()
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
    public async Task Crit6_ttl_expiry_forces_reevaluation()
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
    public async Task Crit7_disabled_evaluates_every_check_live()
    {
        var h = await BuildAsync(Unconditioned(), configure: o => o.Enabled = false);
        var cache = h.NewCache();

        await cache.CheckAsync(h.Request());
        await cache.CheckAsync(h.Request());

        h.Authorizer.CheckCalls.ShouldBe(2);
    }

    [Fact]
    public async Task Crit8_concurrent_identical_checks_share_one_engine_call()
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
    public async Task Crit8_write_during_inflight_compute_is_detected_on_the_next_read()
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
    public async Task Crit8_a_faulting_engine_call_is_not_pinned()
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
    public async Task Crit9_explain_requests_bypass_the_cache()
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
        var cache = new CustodexDecisionCache(
            h.Authorizer, schemaStore: null, cacheStore: null,
            Microsoft.Extensions.Options.Options.Create(h.Options), h.Time);

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

    private static readonly IReadOnlyDictionary<string, object?> EmptyAttrs =
        new Dictionary<string, object?>(StringComparer.Ordinal);
}
