using Custodex.Abstractions;

using Dapper;

using Shouldly;

namespace Custodex.Storage.MySql.Tests;

[Collection("mysql")]
public class IdentifierLengthValidationTests(MySqlFixture fx) : IAsyncLifetime
{
    private MySqlUnitOfWorkFactory _factory = null!;

    public Task InitializeAsync()
    {
        _factory = new MySqlUnitOfWorkFactory(fx.ConnectionString);
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task SeedTenantAsync(TenantContext t)
    {
        await using var u = await _factory.BeginAsync();
        var uow = MySqlUnitOfWork.From(u);
        await uow.Connection.ExecuteAsync("INSERT IGNORE INTO stores (id) VALUES (@s)",
            new { s = t.Store }, uow.Transaction);
        await uow.Connection.ExecuteAsync(
            "INSERT IGNORE INTO tenants (store_id, tenant_id) VALUES (@s, @t)",
            new { s = t.Store, t = t.Tenant }, uow.Transaction);
        await u.CommitAsync();
    }

    [Fact]
    public async Task Over_length_object_id_write_throws_and_persists_nothing()
    {
        var t = new TenantContext("vstore", "vtenant");
        await SeedTenantAsync(t);
        var store = new MySqlRelationStore(fx.ConnectionString);

        var overLength = new string('a', MySqlColumnLimits.EntityId + 1);
        var tuple = new RelationTuple(
            new EntityRef("res", overLength), "editor", new SubjectRef("user", "x"));

        await using var u = await _factory.BeginAsync();
        var ex = await Should.ThrowAsync<ArgumentException>(async () => await store.WriteAsync(t, [tuple], [], u));
        ex.Message.ShouldContain(MySqlColumnLimits.EntityId.ToString());

        (await store.GetByObjectAsync(t, new EntityRef("res", overLength), "editor")).ShouldBeEmpty();
    }

    [Fact]
    public async Task Max_length_object_id_write_succeeds_at_the_boundary()
    {
        var t = new TenantContext("vstore", "vtenant-boundary");
        await SeedTenantAsync(t);
        var store = new MySqlRelationStore(fx.ConnectionString);

        var maxLength = new string('b', MySqlColumnLimits.EntityId);
        var tuple = new RelationTuple(
            new EntityRef("res", maxLength), "editor", new SubjectRef("user", "x"));

        await using (var u = await _factory.BeginAsync())
        {
            await store.WriteAsync(t, [tuple], [], u);
            await u.CommitAsync();
        }

        var found = await store.GetByObjectAsync(t, new EntityRef("res", maxLength), "editor");
        found.ShouldHaveSingleItem().Object.Id.ShouldBe(maxLength);
    }

    [Fact]
    public async Task Over_length_store_id_throws_before_touching_the_database()
    {
        var store = new MySqlRelationStore(fx.ConnectionString);
        var t = new TenantContext(new string('s', MySqlColumnLimits.StoreId + 1), "t");
        var tuple = new RelationTuple(new EntityRef("res", "r1"), "editor", new SubjectRef("user", "x"));

        await using var u = await _factory.BeginAsync();
        await Should.ThrowAsync<ArgumentException>(async () => await store.WriteAsync(t, [tuple], [], u));
    }

    [Fact]
    public async Task Migration_column_widths_match_the_validation_limits()
    {
        await using var conn = await fx.OpenAsync();

        var expected = new (string Table, string Column, int Limit)[]
        {
            ("stores", "id", MySqlColumnLimits.StoreId),
            ("tenants", "store_id", MySqlColumnLimits.StoreId),
            ("tenants", "tenant_id", MySqlColumnLimits.TenantId),
            ("relation_tuples", "object_type", MySqlColumnLimits.EntityType),
            ("relation_tuples", "object_id", MySqlColumnLimits.EntityId),
            ("relation_tuples", "relation", MySqlColumnLimits.Relation),
            ("relation_tuples", "subject_type", MySqlColumnLimits.EntityType),
            ("relation_tuples", "subject_id", MySqlColumnLimits.EntityId),
            ("relation_tuples", "subject_relation", MySqlColumnLimits.Relation),
            ("relation_tuples", "condition_name", MySqlColumnLimits.ConditionName),
            ("object_attributes", "object_id", MySqlColumnLimits.EntityId),
            ("schema_versions", "version", MySqlColumnLimits.SchemaVersion),
        };

        foreach (var (table, column, limit) in expected)
        {
            var actual = await conn.QuerySingleAsync<long>(
                """
                SELECT character_maximum_length FROM information_schema.columns
                WHERE table_schema = DATABASE() AND table_name = @table AND column_name = @column
                """,
                new { table, column });
            actual.ShouldBe(limit, $"{table}.{column} width must match its validation limit");
        }
    }
}
