using Custodex.Abstractions;

using Dapper;

using Microsoft.Data.SqlClient;

using Shouldly;

namespace Custodex.Storage.SqlServer.Tests.Cache;

[Collection("sqlserver")]
public class CacheSweepServiceTests(SqlServerFixture fx)
{
    private SqlServerUnitOfWorkFactory Factory => new(fx.ConnectionString);
    private readonly TenantContext _t = new("s1", "sweep-svc");

    private async Task InsertExpiredAsync(string key)
    {
        await using var conn = new SqlConnection(fx.ConnectionString);
        await conn.OpenAsync();
        await conn.ExecuteAsync("""
            INSERT INTO custodex.cache_entries (store_id, tenant_id, cache_key, value, epoch, expires_at)
            VALUES (@s, @t, @k, @v, 1, DATEADD(SECOND, -10, SYSUTCDATETIME()))
            """, new { s = _t.Store, t = _t.Tenant, k = key, v = new byte[] { 1 } });
    }

    private async Task<long> RowCountAsync()
    {
        await using var conn = new SqlConnection(fx.ConnectionString);
        await conn.OpenAsync();
        return await conn.ExecuteScalarAsync<long>(
            "SELECT COUNT_BIG(*) FROM custodex.cache_entries WHERE store_id = @s AND tenant_id = @t",
            new { s = _t.Store, t = _t.Tenant });
    }

    [Fact]
    public async Task Service_sweeps_expired_rows_on_its_interval()
    {
        await Seed.TenantAsync(Factory, _t);
        await InsertExpiredAsync("dead");
        (await RowCountAsync()).ShouldBe(1);

        var options = new CacheSweepOptions
        {
            ConnectionString = fx.ConnectionString,
            Interval = TimeSpan.FromMilliseconds(50)
        };
        var service = new CacheSweepService(options);

        using var cts = new CancellationTokenSource();
        await service.StartAsync(cts.Token);

        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (await RowCountAsync() > 0 && DateTimeOffset.UtcNow < deadline)
            await Task.Delay(50);

        await service.StopAsync(cts.Token);
        (await RowCountAsync()).ShouldBe(0);
    }
}
