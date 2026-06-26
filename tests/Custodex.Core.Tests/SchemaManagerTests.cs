using Custodex.Abstractions;
using Custodex.TestKit;

using Shouldly;

namespace Custodex.Core.Tests;

public class SchemaManagerTests
{
    private readonly TestWorld _world = TestWorld.New();
    private readonly string _objType;
    private readonly string _grant;
    private readonly string _edit;
    private readonly string _missing;

    public SchemaManagerTests()
    {
        _objType = _world.EntityType();
        _grant = _world.Relation();
        _edit = _world.Permission();
        _missing = _world.Relation();
    }

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

    private Schema ValidSchema() => new SchemaBuilder(TestWorld.Version)
        .Type(_objType, t => t.Relation(_grant, s => s.Type(_world.UserType)).Permission(_edit, p => p.Relation(_grant)))
        .Build();

    private Schema InvalidSchema() => new SchemaBuilder(TestWorld.Version)
        .Type(_objType, t => t.Relation(_grant, s => s.Type(_world.UserType)).Permission(_edit, p => p.Relation(_missing)))
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

        await mgr.SetActiveSchemaAsync(_world.Tenant.Store, ValidSchema());

        (await mgr.GetActiveSchemaAsync(_world.Tenant.Store))!.Version.ShouldBe(TestWorld.Version);
        factory.Last.Committed.ShouldBeTrue();
    }

    [Fact]
    public async Task SetActiveSchemaAsync_throws_on_an_invalid_schema()
    {
        var mgr = new SchemaManager(new FakeSchemaStore(), new FakeUowFactory());

        var ex = await Should.ThrowAsync<SchemaValidationException>(
            () => mgr.SetActiveSchemaAsync(_world.Tenant.Store, InvalidSchema()));

        ex.Errors.ShouldContain(e => e.Contains(_missing));
    }
}
