using Custodex.Abstractions;

using Shouldly;

namespace Custodex.Storage.SqlServer.Tests.Index;

[Collection("sqlserver")]
public class SqlServerIndexStoreTests(SqlServerFixture fx)
{
    private SqlServerUnitOfWorkFactory Factory => new(fx.ConnectionString);
    private SqlServerIndexStore Index => new(fx.ConnectionString);
    private const string V = "v1";

    private async Task UpsertAsync(TenantContext t, params ReverseIndexRow[] rows)
    {
        await using var u = await Factory.BeginAsync();
        await Index.UpsertAsync(t, V, rows, u);
        await u.CommitAsync();
    }

    private static ReverseIndexRow Row(string subject, string perm, string ot, string oid, bool cond = false) =>
        new(subject, perm, ot, oid, cond);

    [Fact]
    public async Task Upsert_then_query_returns_rows_sorted_by_object_id()
    {
        var t = new TenantContext("idx", "sort");
        await Seed.TenantAsync(Factory, t);

        await UpsertAsync(t,
            Row("user:alice", "edit", "doc", "beta"),
            Row("user:alice", "edit", "doc", "alpha"),
            Row("user:bob", "edit", "doc", "gamma"));

        var rows = await Index.QueryObjectsAsync(t, V, "user:alice", "edit", "doc", limit: 100, afterObjectId: null);
        rows.Select(r => r.ObjectId).ShouldBe(["alpha", "beta"]);
    }

    [Fact]
    public async Task Query_resumes_strictly_after_the_cursor_and_caps_at_limit()
    {
        var t = new TenantContext("idx", "cursor");
        await Seed.TenantAsync(Factory, t);

        await UpsertAsync(t,
            Row("user:alice", "edit", "doc", "a"),
            Row("user:alice", "edit", "doc", "b"),
            Row("user:alice", "edit", "doc", "c"));

        var page = await Index.QueryObjectsAsync(t, V, "user:alice", "edit", "doc", limit: 2, afterObjectId: "a");
        page.Select(r => r.ObjectId).ShouldBe(["b", "c"]);
    }

    [Fact]
    public async Task Query_orders_and_paginates_object_ids_ordinally_regardless_of_server_collation()
    {
        var t = new TenantContext("idx", "collation");
        await Seed.TenantAsync(Factory, t);

        await UpsertAsync(t,
            Row("user:alice", "edit", "doc", "alpha"),
            Row("user:alice", "edit", "doc", "Bravo"),
            Row("user:alice", "edit", "doc", "Zulu"));

        var all = await Index.QueryObjectsAsync(t, V, "user:alice", "edit", "doc", 100, null);
        all.Select(r => r.ObjectId).ShouldBe(["Bravo", "Zulu", "alpha"]);

        var afterZulu = await Index.QueryObjectsAsync(t, V, "user:alice", "edit", "doc", 100, "Zulu");
        afterZulu.Select(r => r.ObjectId).ShouldBe(["alpha"]);
    }

    [Fact]
    public async Task Upsert_updates_conditioned_in_place_on_the_natural_key()
    {
        var t = new TenantContext("idx", "cond");
        await Seed.TenantAsync(Factory, t);

        await UpsertAsync(t, Row("user:alice", "edit", "doc", "alpha", cond: false));
        await UpsertAsync(t, Row("user:alice", "edit", "doc", "alpha", cond: true));

        var rows = await Index.QueryObjectsAsync(t, V, "user:alice", "edit", "doc", 100, null);
        rows.ShouldHaveSingleItem().Conditioned.ShouldBeTrue();
    }

    [Fact]
    public async Task DeleteForObject_removes_every_row_for_that_object()
    {
        var t = new TenantContext("idx", "delobj");
        await Seed.TenantAsync(Factory, t);

        await UpsertAsync(t,
            Row("user:alice", "edit", "doc", "alpha"),
            Row("user:bob", "edit", "doc", "alpha"),
            Row("user:alice", "edit", "doc", "beta"));

        await using (var u = await Factory.BeginAsync())
        {
            await Index.DeleteForObjectAsync(t, V, "doc", "alpha", u);
            await u.CommitAsync();
        }

        (await Index.ReadForObjectAsync(t, V, "doc", "alpha")).ShouldBeEmpty();
        (await Index.QueryObjectsAsync(t, V, "user:alice", "edit", "doc", 100, null))
            .Select(r => r.ObjectId).ShouldBe(["beta"]);
    }

    [Fact]
    public async Task DeleteRows_removes_only_the_named_rows()
    {
        var t = new TenantContext("idx", "delrows");
        await Seed.TenantAsync(Factory, t);

        await UpsertAsync(t,
            Row("user:alice", "edit", "doc", "alpha"),
            Row("user:alice", "view", "doc", "alpha"));

        await using (var u = await Factory.BeginAsync())
        {
            await Index.DeleteRowsAsync(t, V, [Row("user:alice", "edit", "doc", "alpha")], u);
            await u.CommitAsync();
        }

        var rows = await Index.ReadForObjectAsync(t, V, "doc", "alpha");
        rows.ShouldHaveSingleItem().Permission.ShouldBe("view");
    }

    [Fact]
    public async Task Build_marker_round_trips_per_schema_version()
    {
        var t = new TenantContext("idx", "marker");
        await Seed.TenantAsync(Factory, t);

        (await Index.IsBuiltAsync(t, V)).ShouldBeFalse();

        await using (var u = await Factory.BeginAsync())
        {
            await Index.MarkBuiltAsync(t, V, u);
            await u.CommitAsync();
        }

        (await Index.IsBuiltAsync(t, V)).ShouldBeTrue();
        (await Index.IsBuiltAsync(t, "v2")).ShouldBeFalse();
    }

    [Fact]
    public async Task Clear_drops_all_rows_across_versions()
    {
        var t = new TenantContext("idx", "clear");
        await Seed.TenantAsync(Factory, t);

        await UpsertAsync(t, Row("user:alice", "edit", "doc", "alpha"));
        await using (var u = await Factory.BeginAsync())
        {
            await Index.UpsertAsync(t, "v2", [Row("user:alice", "edit", "doc", "beta")], u);
            await u.CommitAsync();
        }

        await using (var u = await Factory.BeginAsync())
        {
            await Index.ClearAsync(t, u);
            await u.CommitAsync();
        }

        (await Index.QueryObjectsAsync(t, V, "user:alice", "edit", "doc", 100, null)).ShouldBeEmpty();
        (await Index.QueryObjectsAsync(t, "v2", "user:alice", "edit", "doc", 100, null)).ShouldBeEmpty();
    }
}
