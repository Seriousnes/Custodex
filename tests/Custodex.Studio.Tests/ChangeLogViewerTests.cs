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
}
