using Custodex.Abstractions;
using Custodex.Storage.SqlServer.Index;

using Shouldly;

namespace Custodex.Storage.SqlServer.Tests.Index;

[Collection("sqlserver")]
public class AffectedClosureTests(SqlServerFixture fx)
{
    private SqlServerUnitOfWorkFactory Factory => new(fx.ConnectionString);
    private static readonly TenantContext T = new("affected", "t");

    private static RelationTuple Tup(string ot, string oid, string rel, SubjectRef s) => new(new EntityRef(ot, oid), rel, s);

    private async Task SeedAsync()
    {
        await Seed.TenantAsync(Factory, T);
        var relations = new SqlServerRelationStore(fx.ConnectionString);
        await using var u = await Factory.BeginAsync();
        await relations.WriteAsync(T,
        [
            Tup("item", "i1", "area", new SubjectRef("area", "a1")),
            Tup("item", "i2", "area", new SubjectRef("area", "a1")),
            Tup("doc", "alpha", "editor", new SubjectRef("group", "team", "member")),
            Tup("group", "team", "member", new SubjectRef("user", "alice")),
        ], [], u);
        await u.CommitAsync();
    }

    [Fact]
    public async Task A_direct_grant_change_affects_just_that_object()
    {
        await SeedAsync();
        await using var u = await Factory.BeginAsync();
        var uow = SqlServerUnitOfWork.From(u);
        var changed = new[] { Tup("doc", "alpha", "blocked", new SubjectRef("user", "alice")) };
        var affected = await AffectedClosure.ComputeAsync(uow.Connection, uow.Transaction, T, changed);
        affected.ShouldContain(new EntityRef("doc", "alpha"));
        await u.CommitAsync();
    }

    [Fact]
    public async Task A_structural_edge_change_affects_objects_reaching_it_through_the_arrow()
    {
        await SeedAsync();
        await using var u = await Factory.BeginAsync();
        var uow = SqlServerUnitOfWork.From(u);
        var changed = new[] { Tup("area", "a1", "editor", new SubjectRef("user", "bob")) };
        var affected = await AffectedClosure.ComputeAsync(uow.Connection, uow.Transaction, T, changed);
        affected.ShouldContain(new EntityRef("area", "a1"));
        affected.ShouldContain(new EntityRef("item", "i1"));
        affected.ShouldContain(new EntityRef("item", "i2"));
        await u.CommitAsync();
    }

    [Fact]
    public async Task A_group_membership_change_affects_objects_granting_to_that_group()
    {
        await SeedAsync();
        await using var u = await Factory.BeginAsync();
        var uow = SqlServerUnitOfWork.From(u);
        var changed = new[] { Tup("group", "team", "member", new SubjectRef("user", "carol")) };
        var affected = await AffectedClosure.ComputeAsync(uow.Connection, uow.Transaction, T, changed);
        affected.ShouldContain(new EntityRef("group", "team"));
        affected.ShouldContain(new EntityRef("doc", "alpha"));
        await u.CommitAsync();
    }
}
