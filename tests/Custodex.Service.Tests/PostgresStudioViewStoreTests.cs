using System.Text.Json;

using Custodex.Abstractions;
using Custodex.Studio.Views;
using Custodex.TestKit;

using Shouldly;

namespace Custodex.Service.Tests;

[Collection("service")]
public sealed class PostgresStudioViewStoreTests(PostgresFixture pg)
{
    private const string Owner = StudioView.SharedOwner;

    private PostgresStudioViewStore CreateStore() => new(pg.ConnectionString);

    private static string Config(string value) =>
        JsonSerializer.Serialize(new Dictionary<string, string> { ["payload"] = value });

    private static string Payload(string configJson) =>
        JsonDocument.Parse(configJson).RootElement.GetProperty("payload").GetString()!;

    [Fact]
    public async Task Save_then_get_round_trips_the_row()
    {
        var world = TestWorld.New();
        var store = CreateStore();
        var kind = world.EntityType();
        var key = world.ObjectId();
        var payload = world.SubjectId();
        var when = DateTimeOffset.UnixEpoch.AddDays(3);

        await store.SaveAsync(new StudioView(
            world.Tenant.Store, world.Tenant.Tenant, Owner, kind, key, Config(payload), when));

        var got = await store.GetAsync(world.Tenant.Store, world.Tenant.Tenant, Owner, kind, key);

        got.ShouldNotBeNull();
        got!.Store.ShouldBe(world.Tenant.Store);
        got.Tenant.ShouldBe(world.Tenant.Tenant);
        got.Owner.ShouldBe(Owner);
        got.Kind.ShouldBe(kind);
        got.Key.ShouldBe(key);
        Payload(got.ConfigJson).ShouldBe(payload);
        got.UpdatedAt.UtcDateTime.ShouldBe(when.UtcDateTime);
    }

    [Fact]
    public async Task Save_upserts_in_place_without_duplicating_the_row()
    {
        var world = TestWorld.New();
        var store = CreateStore();
        var kind = world.EntityType();
        var key = world.ObjectId();
        var first = world.SubjectId();
        var second = world.SubjectId();
        var firstWhen = DateTimeOffset.UnixEpoch.AddDays(1);
        var secondWhen = DateTimeOffset.UnixEpoch.AddDays(2);

        await store.SaveAsync(new StudioView(
            world.Tenant.Store, world.Tenant.Tenant, Owner, kind, key, Config(first), firstWhen));
        await store.SaveAsync(new StudioView(
            world.Tenant.Store, world.Tenant.Tenant, Owner, kind, key, Config(second), secondWhen));

        var all = await store.ListAsync(world.Tenant.Store, world.Tenant.Tenant, kind);

        all.Count.ShouldBe(1);
        Payload(all[0].ConfigJson).ShouldBe(second);
        all[0].UpdatedAt.UtcDateTime.ShouldBe(secondWhen.UtcDateTime);
    }

    [Fact]
    public async Task List_returns_scope_views_newest_first_and_filters_by_kind()
    {
        var world = TestWorld.New();
        var store = CreateStore();
        var layoutKind = world.EntityType();
        var otherKind = world.EntityType();
        var older = world.ObjectId();
        var newer = world.ObjectId();
        var otherKey = world.ObjectId();

        await store.SaveAsync(new StudioView(
            world.Tenant.Store, world.Tenant.Tenant, Owner, layoutKind, older, Config(world.SubjectId()),
            DateTimeOffset.UnixEpoch.AddHours(1)));
        await store.SaveAsync(new StudioView(
            world.Tenant.Store, world.Tenant.Tenant, Owner, layoutKind, newer, Config(world.SubjectId()),
            DateTimeOffset.UnixEpoch.AddHours(2)));
        await store.SaveAsync(new StudioView(
            world.Tenant.Store, world.Tenant.Tenant, Owner, otherKind, otherKey, Config(world.SubjectId()),
            DateTimeOffset.UnixEpoch.AddHours(3)));

        var filtered = await store.ListAsync(world.Tenant.Store, world.Tenant.Tenant, layoutKind);

        filtered.Select(v => v.Key).ShouldBe([newer, older]);

        var all = await store.ListAsync(world.Tenant.Store, world.Tenant.Tenant);
        all.Select(v => v.Key).ShouldContain(otherKey);
        all.Count.ShouldBe(3);
    }

    [Fact]
    public async Task Delete_removes_the_row()
    {
        var world = TestWorld.New();
        var store = CreateStore();
        var kind = world.EntityType();
        var key = world.ObjectId();

        await store.SaveAsync(new StudioView(
            world.Tenant.Store, world.Tenant.Tenant, Owner, kind, key, Config(world.SubjectId()),
            DateTimeOffset.UnixEpoch));

        await store.DeleteAsync(world.Tenant.Store, world.Tenant.Tenant, Owner, kind, key);

        var got = await store.GetAsync(world.Tenant.Store, world.Tenant.Tenant, Owner, kind, key);
        got.ShouldBeNull();
    }

    [Fact]
    public async Task A_view_is_isolated_to_its_store_and_tenant()
    {
        var world = TestWorld.New();
        var store = CreateStore();
        var kind = world.EntityType();
        var key = world.ObjectId();
        var otherStore = new TenantContext(world.EntityType(), world.Tenant.Tenant);
        var otherTenant = new TenantContext(world.Tenant.Store, world.EntityType());

        await store.SaveAsync(new StudioView(
            world.Tenant.Store, world.Tenant.Tenant, Owner, kind, key, Config(world.SubjectId()),
            DateTimeOffset.UnixEpoch));

        (await store.GetAsync(otherStore.Store, otherStore.Tenant, Owner, kind, key)).ShouldBeNull();
        (await store.GetAsync(otherTenant.Store, otherTenant.Tenant, Owner, kind, key)).ShouldBeNull();
        (await store.ListAsync(otherStore.Store, otherStore.Tenant, kind)).ShouldBeEmpty();
        (await store.ListAsync(otherTenant.Store, otherTenant.Tenant, kind)).ShouldBeEmpty();
        (await store.ListAsync(world.Tenant.Store, world.Tenant.Tenant, kind)).Count.ShouldBe(1);
    }
}
