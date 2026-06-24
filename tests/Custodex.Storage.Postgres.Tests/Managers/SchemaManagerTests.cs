using Custodex.Abstractions;
using Custodex.Core;
using Custodex.Storage.Postgres;
using Custodex.Storage.Postgres.Managers;
using Dapper;
using Shouldly;
using Xunit;

namespace Custodex.Storage.Postgres.Tests.Managers;

[Collection("postgres")]
public class SchemaManagerTests(PostgresFixture fx) : IAsyncLifetime
{
    private NpgsqlUnitOfWorkFactory _factory = null!;
    private CustodexSchemaManager _manager = null!;
    private NpgsqlSchemaStore _schemas = null!;

    public async Task InitializeAsync()
    {
        await using var conn = await fx.OpenAsync();
        await MigrationRunner.ApplyAsync(conn);
        _factory = new NpgsqlUnitOfWorkFactory(fx.ConnectionString);
        _schemas = new NpgsqlSchemaStore(fx.ConnectionString);
        var relations = new NpgsqlRelationStore(fx.ConnectionString);
        var attributes = new NpgsqlAttributeStore(fx.ConnectionString);
        var changeLog = new NpgsqlChangeLogStore(fx.ConnectionString);
        var cache = new PostgresCacheStore(fx.ConnectionString, new TenantContext("sm", "sm"));
        var audited = new AuditedWritePath(relations, attributes, _schemas, changeLog, cache);
        _manager = new CustodexSchemaManager(_factory, _schemas, audited);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static async Task SeedStoreOnlyAsync(NpgsqlUnitOfWorkFactory factory, string store)
    {
        await using var u = await factory.BeginAsync();
        var uow = NpgsqlUnitOfWork.From(u);
        await uow.Connection.ExecuteAsync("INSERT INTO stores (id) VALUES (@s) ON CONFLICT DO NOTHING",
            new { s = store }, uow.Transaction);
        await u.CommitAsync();
    }

    [Fact]
    public async Task Valid_schema_activates_without_pre_existing_tenant()
    {
        const string store = "sm-valid";
        await SeedStoreOnlyAsync(_factory, store);
        var schema = new SchemaBuilder("v1")
            .Type("doc", t => t.Relation("viewer", s => s.User()).Permission("view", p => p.Relation("viewer")))
            .Build();

        await _manager.SetActiveSchemaAsync(store, schema);
        (await _manager.GetActiveSchemaAsync(store))!.Version.ShouldBe("v1");
    }

    [Fact]
    public async Task Invalid_schema_throws_and_writes_nothing()
    {
        const string store = "sm-invalid";
        await SeedStoreOnlyAsync(_factory, store);
        var bad = new Schema("v1",
            [new EntityTypeDef("doc", [], [new PermissionDef("view", new RelationRef("ghost"))])],
            []);

        await Should.ThrowAsync<SchemaValidationException>(() => _manager.SetActiveSchemaAsync(store, bad));
        (await _manager.GetActiveSchemaAsync(store)).ShouldBeNull();
    }
}
