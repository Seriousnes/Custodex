using Bunit;

using Custodex.Abstractions;
using Custodex.Studio;
using Custodex.Studio.Components.Pages;
using Custodex.TestKit;

using Microsoft.Extensions.DependencyInjection;

using Shouldly;

namespace Custodex.Studio.Tests;

public sealed class ChangeLogViewerTests
{
    private sealed class FakeRelationManager : IRelationManager
    {
        private readonly IReadOnlyList<ChangeLogEntry> _entries;

        public FakeRelationManager(IReadOnlyList<ChangeLogEntry> entries) => _entries = entries;

        public ChangeLogFilter? CapturedFilter { get; private set; }

        public Task WriteTuplesAsync(TenantContext tenant, string actor, IReadOnlyList<RelationTuple> tuples, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task DeleteTuplesAsync(TenantContext tenant, string actor, IReadOnlyList<RelationTuple> tuples, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task WriteAttributesAsync(TenantContext tenant, string actor, EntityRef obj, IReadOnlyDictionary<string, object?> attributes, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<RelationTuple>> ReadTuplesAsync(TenantContext tenant, TupleFilter filter, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<RelationTuple>>([]);

        public Task<IReadOnlyList<ChangeLogEntry>> ReadChangeLogAsync(TenantContext tenant, ChangeLogFilter filter, CancellationToken ct = default)
        {
            CapturedFilter = filter;
            return Task.FromResult(_entries);
        }
    }

    private static BunitContext CreateContext(StudioConnectionState state, FakeRelationManager manager)
    {
        var ctx = new BunitContext();
        ctx.Services.AddSingleton(state);
        ctx.Services.AddSingleton<IRelationManager>(manager);
        return ctx;
    }

    [Fact]
    public void Not_connected_shows_notice_and_no_load_button()
    {
        using var ctx = CreateContext(new StudioConnectionState(), new FakeRelationManager([]));

        var cut = ctx.Render<ChangeLogViewer>();

        cut.Markup.ShouldContain("Overview");
        cut.FindAll("#load").ShouldBeEmpty();
    }

    [Fact]
    public void Entries_render_actor_operation_target_and_id()
    {
        var world = TestWorld.New();
        var ts = new DateTimeOffset(2025, 3, 15, 10, 0, 0, TimeSpan.Zero);

        var entries = new List<ChangeLogEntry>
        {
            new(1L, world.SubjectId(), world.Relation(), world.ObjectId(), null, null, ts),
            new(2L, world.SubjectId(), world.Relation(), world.ObjectId(), null, null, ts.AddMinutes(1))
        };

        var fake = new FakeRelationManager(entries);
        var state = new StudioConnectionState();
        state.Connect(world.Tenant.Store, world.Tenant.Tenant);

        using var ctx = CreateContext(state, fake);
        var cut = ctx.Render<ChangeLogViewer>();

        cut.Find("#load").Click();

        cut.WaitForAssertion(() =>
        {
            cut.Markup.ShouldContain(entries[0].Actor);
            cut.Markup.ShouldContain(entries[0].Operation);
            cut.Markup.ShouldContain(entries[0].Target);
            cut.Markup.ShouldContain(entries[0].Id.ToString());
            cut.Markup.ShouldContain(entries[1].Actor);
            cut.Markup.ShouldContain(entries[1].Operation);
            cut.Markup.ShouldContain(entries[1].Target);
            cut.Markup.ShouldContain(entries[1].Id.ToString());
        });
    }

    [Fact]
    public void Before_after_diff_highlights_changed_key()
    {
        var world = TestWorld.New();
        var ts = new DateTimeOffset(2025, 3, 15, 10, 0, 0, TimeSpan.Zero);

        var changedKey = world.EntityType();
        var sameKey = world.Relation();

        var before = new Dictionary<string, object?> { [changedKey] = "old", [sameKey] = "same" };
        var after = new Dictionary<string, object?> { [changedKey] = "new", [sameKey] = "same" };

        var entry = new ChangeLogEntry(1L, world.SubjectId(), world.Relation(), world.ObjectId(), before, after, ts);
        var fake = new FakeRelationManager([entry]);
        var state = new StudioConnectionState();
        state.Connect(world.Tenant.Store, world.Tenant.Tenant);

        using var ctx = CreateContext(state, fake);
        var cut = ctx.Render<ChangeLogViewer>();

        cut.Find("#load").Click();

        cut.WaitForAssertion(() =>
        {
            cut.FindAll(".changed").ShouldContain(el => el.TextContent.Contains(changedKey));
            cut.FindAll(".changed").ShouldContain(el => el.TextContent.Contains("old"));
            cut.FindAll(".changed").ShouldContain(el => el.TextContent.Contains("new"));
        });
    }

    [Fact]
    public void Insert_shows_none_on_before_and_delete_shows_none_on_after()
    {
        var world = TestWorld.New();
        var ts = new DateTimeOffset(2025, 3, 15, 10, 0, 0, TimeSpan.Zero);
        var propKey = world.EntityType();

        var insertEntry = new ChangeLogEntry(
            1L, world.SubjectId(), world.Relation(), world.ObjectId(),
            null, new Dictionary<string, object?> { [propKey] = "v" }, ts);
        var deleteEntry = new ChangeLogEntry(
            2L, world.SubjectId(), world.Relation(), world.ObjectId(),
            new Dictionary<string, object?> { [propKey] = "v" }, null, ts.AddMinutes(1));

        var fake = new FakeRelationManager([insertEntry, deleteEntry]);
        var state = new StudioConnectionState();
        state.Connect(world.Tenant.Store, world.Tenant.Tenant);

        using var ctx = CreateContext(state, fake);
        var cut = ctx.Render<ChangeLogViewer>();

        cut.Find("#load").Click();

        cut.WaitForAssertion(() =>
        {
            cut.FindAll(".diff-none").Count.ShouldBe(2);

            var entryEls = cut.FindAll(".changelog-entry");

            var insertCols = entryEls[0].QuerySelectorAll(".diff-col");
            insertCols[0].QuerySelector(".diff-none").ShouldNotBeNull();
            insertCols[1].QuerySelector(".diff-none").ShouldBeNull();

            var deleteCols = entryEls[1].QuerySelectorAll(".diff-col");
            deleteCols[0].QuerySelector(".diff-none").ShouldBeNull();
            deleteCols[1].QuerySelector(".diff-none").ShouldNotBeNull();
        });
    }

    [Fact]
    public void Filter_passthrough_captures_correct_filter()
    {
        var world = TestWorld.New();
        var fake = new FakeRelationManager([]);
        var state = new StudioConnectionState();
        state.Connect(world.Tenant.Store, world.Tenant.Tenant);

        using var ctx = CreateContext(state, fake);
        var cut = ctx.Render<ChangeLogViewer>();

        var sinceText = "2025-01-01T00:00:00+00:00";
        var actorId = world.SubjectId();

        cut.Find("#filter-since").Change(sinceText);
        cut.Find("#filter-actor").Change(actorId);
        cut.Find("#filter-limit").Change("50");
        cut.Find("#load").Click();

        cut.WaitForAssertion(() =>
        {
            fake.CapturedFilter.ShouldNotBeNull();
            fake.CapturedFilter!.Since.ShouldNotBeNull();
            fake.CapturedFilter!.Since!.Value.ShouldBe(DateTimeOffset.Parse(sinceText));
            fake.CapturedFilter!.Actor.ShouldBe(actorId);
            fake.CapturedFilter!.Limit.ShouldBe(50);
        });
    }
}
