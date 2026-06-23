using Custodex.Abstractions;
using Custodex.Core;
using Shouldly;
using Xunit;

namespace Custodex.Core.Tests;

public class SchemaManagerTests
{
    private sealed class FakeSchemaStore : ISchemaStore
    {
        private readonly Dictionary<string, Schema> _store = new(StringComparer.Ordinal);
        public Task<Schema?> GetActiveAsync(string store, CancellationToken ct = default) =>
            Task.FromResult(_store.TryGetValue(store, out var s) ? s : null);
        public Task SetActiveAsync(string store, Schema schema, IUnitOfWork uow, CancellationToken ct = default)
        {
            _store[store] = schema;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeUow : IUnitOfWork
    {
        public bool Committed { get; private set; }
        public Task CommitAsync(CancellationToken ct = default) { Committed = true; return Task.CompletedTask; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeUowFactory : IUnitOfWorkFactory
    {
        public FakeUow Last { get; private set; } = new();
        public Task<IUnitOfWork> BeginAsync(CancellationToken ct = default)
        {
            Last = new FakeUow();
            return Task.FromResult<IUnitOfWork>(Last);
        }
    }

    private static Schema ValidSchema() => new SchemaBuilder("v1")
        .Type("animal", t => t.Relation("medicator", s => s.User()).Permission("edit", p => p.Relation("medicator")))
        .Build();

    private static Schema InvalidSchema() => new SchemaBuilder("v1")
        .Type("animal", t => t.Relation("medicator", s => s.User()).Permission("edit", p => p.Relation("ghost")))
        .Build();

    [Fact]
    public void ValidateSchema_returns_validator_result()
    {
        var mgr = new SchemaManager(new FakeSchemaStore(), new FakeUowFactory());

        mgr.ValidateSchema(ValidSchema()).IsValid.ShouldBeTrue();
        mgr.ValidateSchema(InvalidSchema()).IsValid.ShouldBeFalse();
    }

    [Fact]
    public async Task SetActiveSchemaAsync_persists_and_commits_a_valid_schema()
    {
        var store = new FakeSchemaStore();
        var factory = new FakeUowFactory();
        var mgr = new SchemaManager(store, factory);

        await mgr.SetActiveSchemaAsync("zoo", ValidSchema());

        (await mgr.GetActiveSchemaAsync("zoo"))!.Version.ShouldBe("v1");
        factory.Last.Committed.ShouldBeTrue();
    }

    [Fact]
    public async Task SetActiveSchemaAsync_throws_on_an_invalid_schema()
    {
        var mgr = new SchemaManager(new FakeSchemaStore(), new FakeUowFactory());

        var ex = await Should.ThrowAsync<SchemaValidationException>(
            () => mgr.SetActiveSchemaAsync("zoo", InvalidSchema()));

        ex.Errors.ShouldContain(e => e.Contains("ghost"));
    }
}
