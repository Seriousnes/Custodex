using Bunit;

using Custodex.Abstractions;
using Custodex.Studio;
using Custodex.Studio.Components.Pages;
using Custodex.TestKit;

using Microsoft.Extensions.DependencyInjection;

using Shouldly;

namespace Custodex.Studio.Tests;

public sealed class CheckPlaygroundTests
{
    private sealed class FakeAuthorizer : IAuthorizer
    {
        private readonly CheckResult? _result;
        private readonly Exception? _exception;

        public FakeAuthorizer() { }

        public FakeAuthorizer(CheckResult result) => _result = result;

        public FakeAuthorizer(Exception exception) => _exception = exception;

        public Task<CheckResult> CheckAsync(CheckRequest request, CancellationToken ct = default) =>
            _exception is not null
                ? Task.FromException<CheckResult>(_exception)
                : Task.FromResult(_result ?? new CheckResult(false));

        public Task<IReadOnlyList<CheckResult>> BatchCheckAsync(BatchCheckRequest request, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<ListObjectsResult> ListObjectsAsync(ListObjectsRequest request, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<ListSubjectsResult> ListSubjectsAsync(ListSubjectsRequest request, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private static BunitContext CreateContext(StudioConnectionState state, IAuthorizer authorizer)
    {
        var ctx = new BunitContext();
        ctx.Services.AddSingleton(state);
        ctx.Services.AddSingleton(authorizer);
        return ctx;
    }

    [Fact]
    public void Not_connected_shows_notice_and_no_run_button()
    {
        using var ctx = CreateContext(new StudioConnectionState(), new FakeAuthorizer());

        var cut = ctx.Render<CheckPlayground>();

        cut.Markup.ShouldContain("Overview");
        cut.FindAll("#run-check").ShouldBeEmpty();
    }

    [Fact]
    public void Allowed_check_shows_allow_badge_and_explain_tree_with_correct_node_styling()
    {
        var world = TestWorld.New();

        var rootDesc = world.Permission();
        var childDesc = world.Permission();

        var explain = new ExplainNode(rootDesc, true, [new ExplainNode(childDesc, false, [])]);
        var fake = new FakeAuthorizer(new CheckResult(true, explain));

        var state = new StudioConnectionState();
        state.Connect(world.Tenant.Store, world.Tenant.Tenant);

        using var ctx = CreateContext(state, fake);
        var cut = ctx.Render<CheckPlayground>();

        var objType = world.EntityType();
        var objId = world.ObjectId();
        var perm = world.Permission();
        var subjType = world.EntityType();
        var subjId = world.SubjectId();

        cut.Find("#obj-type").Change(objType);
        cut.Find("#obj-id").Change(objId);
        cut.Find("#permission").Change(perm);
        cut.Find("#subj-type").Change(subjType);
        cut.Find("#subj-id").Change(subjId);
        cut.Find("#eval-time").Change("2025-01-01T00:00:00+00:00");

        cut.Find("#run-check").Click();

        cut.WaitForAssertion(() =>
        {
            cut.Find(".badge-allow").ShouldNotBeNull();
            cut.FindAll(".explain-allow").ShouldContain(el => el.TextContent.Contains(rootDesc));
            cut.FindAll(".explain-deny").ShouldContain(el => el.TextContent.Contains(childDesc));
        });
    }

    [Fact]
    public void Denied_check_shows_deny_badge()
    {
        var world = TestWorld.New();
        var fake = new FakeAuthorizer(new CheckResult(false, null));

        var state = new StudioConnectionState();
        state.Connect(world.Tenant.Store, world.Tenant.Tenant);

        using var ctx = CreateContext(state, fake);
        var cut = ctx.Render<CheckPlayground>();

        cut.Find("#obj-type").Change(world.EntityType());
        cut.Find("#obj-id").Change(world.ObjectId());
        cut.Find("#permission").Change(world.Permission());
        cut.Find("#subj-type").Change(world.EntityType());
        cut.Find("#subj-id").Change(world.SubjectId());
        cut.Find("#eval-time").Change("2025-01-01T00:00:00+00:00");

        cut.Find("#run-check").Click();

        cut.WaitForAssertion(() =>
        {
            cut.Find(".badge-deny").ShouldNotBeNull();
        });
    }

    [Fact]
    public void Engine_exception_renders_error_region_without_propagating()
    {
        var world = TestWorld.New();
        var permName = world.Permission();
        var typeName = world.EntityType();
        var exception = new UnknownPermissionException(typeName, permName);
        var fake = new FakeAuthorizer(exception);

        var state = new StudioConnectionState();
        state.Connect(world.Tenant.Store, world.Tenant.Tenant);

        using var ctx = CreateContext(state, fake);
        var cut = ctx.Render<CheckPlayground>();

        cut.Find("#obj-type").Change(world.EntityType());
        cut.Find("#obj-id").Change(world.ObjectId());
        cut.Find("#permission").Change(world.Permission());
        cut.Find("#subj-type").Change(world.EntityType());
        cut.Find("#subj-id").Change(world.SubjectId());
        cut.Find("#eval-time").Change("2025-01-01T00:00:00+00:00");

        cut.Find("#run-check").Click();

        cut.WaitForAssertion(() =>
        {
            cut.Find(".playground-error").ShouldNotBeNull();
            cut.Markup.ShouldContain(exception.Message);
        });
    }
}
