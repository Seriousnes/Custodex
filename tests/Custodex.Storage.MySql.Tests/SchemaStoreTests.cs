using Custodex.Abstractions;

using Dapper;

using Shouldly;

namespace Custodex.Storage.MySql.Tests;

[Collection("mysql")]
public class SchemaStoreTests(MySqlFixture fx) : IAsyncLifetime
{
    private MySqlUnitOfWorkFactory _factory = null!;

    public Task InitializeAsync()
    {
        _factory = new MySqlUnitOfWorkFactory(fx.ConnectionString);
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task SeedStoreAsync(string store)
    {
        await using var u = await _factory.BeginAsync();
        var uow = MySqlUnitOfWork.From(u);
        await uow.Connection.ExecuteAsync("INSERT IGNORE INTO stores (id) VALUES (@s)",
            new { s = store }, uow.Transaction);
        await u.CommitAsync();
    }

    private static Schema SchemaV(string version) => new(version,
        [new EntityTypeDef("resource",
            [new RelationDef("editor", [new SubjectTypeRef("user")])],
            [new PermissionDef("edit", new RelationRef("editor"))])],
        []);

    [Fact]
    public async Task Set_active_then_get_active_round_trips()
    {
        const string store = "schema-store";
        await SeedStoreAsync(store);
        var schemaStore = new MySqlSchemaStore(fx.ConnectionString);

        await using (var u = await _factory.BeginAsync())
        {
            await schemaStore.SetActiveAsync(store, SchemaV("v1"), u);
            await u.CommitAsync();
        }

        var active = await schemaStore.GetActiveAsync(store);
        active.ShouldNotBeNull();
        active!.Version.ShouldBe("v1");
        active.Types.Single().Name.ShouldBe("resource");
    }

    [Fact]
    public async Task Set_active_a_second_version_supersedes_the_first()
    {
        const string store = "schema-store-2";
        await SeedStoreAsync(store);
        var schemaStore = new MySqlSchemaStore(fx.ConnectionString);

        await using (var u = await _factory.BeginAsync())
        {
            await schemaStore.SetActiveAsync(store, SchemaV("v1"), u);
            await u.CommitAsync();
        }
        await using (var u = await _factory.BeginAsync())
        {
            await schemaStore.SetActiveAsync(store, SchemaV("v2"), u);
            await u.CommitAsync();
        }

        (await schemaStore.GetActiveAsync(store))!.Version.ShouldBe("v2");
    }

    [Fact]
    public async Task Get_active_returns_null_for_unknown_store()
    {
        var schemaStore = new MySqlSchemaStore(fx.ConnectionString);
        (await schemaStore.GetActiveAsync("never-set")).ShouldBeNull();
    }
}
