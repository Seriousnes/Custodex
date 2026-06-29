using Custodex.Abstractions;

using Dapper;

namespace Custodex.Storage.SqlServer.Tests;

internal static class Seed
{
    public static async Task TenantAsync(SqlServerUnitOfWorkFactory factory, TenantContext t)
    {
        await using var u = await factory.BeginAsync();
        var uow = SqlServerUnitOfWork.From(u);
        await uow.Connection.ExecuteAsync(
            "IF NOT EXISTS (SELECT 1 FROM custodex.stores WHERE id = @s) INSERT INTO custodex.stores (id) VALUES (@s);",
            new { s = t.Store }, uow.Transaction);
        await uow.Connection.ExecuteAsync(
            "IF NOT EXISTS (SELECT 1 FROM custodex.tenants WHERE store_id = @s AND tenant_id = @t) " +
            "INSERT INTO custodex.tenants (store_id, tenant_id) VALUES (@s, @t);",
            new { s = t.Store, t = t.Tenant }, uow.Transaction);
        await u.CommitAsync();
    }
}
