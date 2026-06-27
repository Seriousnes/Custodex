using Custodex.Abstractions;
using Custodex.Core.Conditions;

using Dapper;

using Npgsql;

using Shouldly;

namespace Custodex.Storage.Postgres.Tests;

[Collection("postgres")]
public class JsonbHostConnectionTests(PostgresFixture fx) : IAsyncLifetime
{
    private NpgsqlUnitOfWorkFactory _factory = null!;
    private NpgsqlRelationStore _relations = null!;
    private NpgsqlAttributeStore _attributes = null!;
    private NpgsqlSchemaStore _schemas = null!;

    public async Task InitializeAsync()
    {
        await using var conn = await fx.OpenAsync();
        await MigrationRunner.ApplyAsync(conn);
        _factory = new NpgsqlUnitOfWorkFactory(fx.ConnectionString);
        _relations = new NpgsqlRelationStore(fx.ConnectionString);
        _attributes = new NpgsqlAttributeStore(fx.ConnectionString);
        _schemas = new NpgsqlSchemaStore(fx.ConnectionString);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task SeedTenantAsync(TenantContext t)
    {
        await using var u = await _factory.BeginAsync();
        var uow = NpgsqlUnitOfWork.From(u);
        await uow.Connection.ExecuteAsync("INSERT INTO stores (id) VALUES (@s) ON CONFLICT DO NOTHING",
            new { s = t.Store }, uow.Transaction);
        await uow.Connection.ExecuteAsync(
            "INSERT INTO tenants (store_id, tenant_id) VALUES (@s, @t) ON CONFLICT DO NOTHING",
            new { s = t.Store, t = t.Tenant }, uow.Transaction);
        await u.CommitAsync();
    }

    private static Schema ConditionedSchema() =>
        new("v1",
            [new EntityTypeDef("resource",
                [new RelationDef("editor", [new SubjectTypeRef("user")])],
                [new PermissionDef("edit", new RelationRef("editor"))])],
            [new ConditionDef("within_hours",
                [new ConditionParam("start", ConditionType.Int)],
                new Compare(new ParamRef("start"), CompareOp.Le, new HourOf(new ContextNow())))]);

    private static RelationTuple ConditionedTuple() =>
        new(new EntityRef("resource", "r1"), "editor", new SubjectRef("user", "u1"),
            new ConditionRef("within_hours",
                new Dictionary<string, object?> { ["start"] = 8, ["label"] = "am", ["enabled"] = true }));

    private static Dictionary<string, object?> Attrs() =>
        new() { ["is_flagged"] = true, ["weight"] = 12.5, ["region"] = "north", ["count"] = 42 };

    [Fact]
    public async Task Jsonb_attributes_schema_and_condition_params_round_trip_over_a_host_supplied_connection()
    {
        var t = new TenantContext("json-host", "t1");
        await SeedTenantAsync(t);
        var obj = new EntityRef("resource", "r1");

        await using var hostConn = new NpgsqlConnection(fx.RawConnectionString);
        await hostConn.OpenAsync();
        await using var hostTx = await hostConn.BeginTransactionAsync();

        await using (var uow = _factory.Enlist(hostConn, hostTx))
        {
            await _attributes.SetAsync(t, obj, Attrs(), uow);
            await _schemas.SetActiveAsync(t.Store, ConditionedSchema(), uow);
            await _relations.WriteAsync(t, [ConditionedTuple()], [], uow);
        }

        await hostTx.CommitAsync();

        var attrs = await _attributes.GetAsync(t, obj);
        attrs.ShouldNotBeNull();
        attrs!["is_flagged"]!.ToString().ShouldBe("True");
        attrs["weight"]!.ToString().ShouldBe("12.5");
        attrs["region"]!.ToString().ShouldBe("north");
        attrs["count"]!.ToString().ShouldBe("42");

        var schema = await _schemas.GetActiveAsync(t.Store);
        schema.ShouldNotBeNull();
        schema!.Version.ShouldBe("v1");
        schema.Types.Single().Permissions.Single().Expression.ShouldBeOfType<RelationRef>().Relation.ShouldBe("editor");
        schema.Conditions.Single().Body.ShouldBeOfType<Compare>()
            .Left.ShouldBeOfType<ParamRef>().Name.ShouldBe("start");

        var tuple = (await _relations.QueryAsync(t, new TupleFilter(ObjectType: "resource"))).ShouldHaveSingleItem();
        tuple.Condition.ShouldNotBeNull();
        tuple.Condition!.Name.ShouldBe("within_hours");
        tuple.Condition.Parameters["start"]!.ToString().ShouldBe("8");
        tuple.Condition.Parameters["label"]!.ToString().ShouldBe("am");
        tuple.Condition.Parameters["enabled"]!.ToString().ShouldBe("True");
    }

    [Fact]
    public async Task Borrowed_connection_jsonb_reads_match_provider_created_connection_reads()
    {
        var t = new TenantContext("json-parity", "t1");
        await SeedTenantAsync(t);
        var obj = new EntityRef("resource", "r1");

        await using (var u = await _factory.BeginAsync())
        {
            await _attributes.SetAsync(t, obj, Attrs(), u);
            await _schemas.SetActiveAsync(t.Store, ConditionedSchema(), u);
            await _relations.WriteAsync(t, [ConditionedTuple()], [], u);
            await u.CommitAsync();
        }

        var providerAttrs = await _attributes.GetAsync(t, obj);
        var providerSchema = await _schemas.GetActiveAsync(t.Store);
        var providerTuple = (await _relations.QueryAsync(t, new TupleFilter(ObjectType: "resource"))).Single();

        await using var hostConn = new NpgsqlConnection(fx.RawConnectionString);
        await hostConn.OpenAsync();
        await using var hostTx = await hostConn.BeginTransactionAsync();

        var borrowedAttrsJson = await hostConn.ExecuteScalarAsync<string?>(new CommandDefinition("""
            SELECT attributes::text FROM custodex.object_attributes
            WHERE store_id = @s AND tenant_id = @t AND object_type = @ot AND object_id = @oid
            """, new { s = t.Store, t = t.Tenant, ot = obj.Type, oid = obj.Id }, transaction: hostTx));
        var borrowedAttrs = Json.Deserialize<Dictionary<string, object?>>(borrowedAttrsJson);

        var borrowedSchemaJson = await hostConn.ExecuteScalarAsync<string?>(new CommandDefinition("""
            SELECT definition::text FROM custodex.schema_versions
            WHERE store_id = @s AND is_active
            """, new { s = t.Store }, transaction: hostTx));
        var borrowedSchema = Json.Deserialize<Schema>(borrowedSchemaJson);

        await using var readUow = _factory.Enlist(hostConn, hostTx);
        var borrowedTuple = (await _relations.OnUnitOfWork(readUow)
            .QueryAsync(t, new TupleFilter(ObjectType: "resource"))).Single();

        Json.Serialize(borrowedAttrs).ShouldBe(Json.Serialize(providerAttrs));
        Json.Serialize(borrowedSchema).ShouldBe(Json.Serialize(providerSchema));
        Json.Serialize(borrowedTuple.Condition!.Parameters).ShouldBe(Json.Serialize(providerTuple.Condition!.Parameters));
    }
}
