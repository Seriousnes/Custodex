using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;

namespace Custodex.Service.Health;

/// <summary>
/// An <see cref="IHealthCheck"/> that probes Postgres reachability and confirms that
/// migrations have been applied by querying <c>schema_migrations</c>. Register this
/// with the <c>"ready"</c> tag so it appears under <c>/health</c> but not <c>/alive</c>.
/// </summary>
public sealed class PostgresReadyHealthCheck(IConfiguration configuration) : IHealthCheck
{
    /// <inheritdoc />
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var connectionString = configuration.GetConnectionString("Custodex")
            ?? configuration["Custodex:ConnectionString"];
        if (string.IsNullOrEmpty(connectionString))
            return HealthCheckResult.Unhealthy("Custodex:ConnectionString is not configured.");

        try
        {
            await using var conn = new NpgsqlConnection(connectionString);
            await conn.OpenAsync(cancellationToken);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT 1 FROM schema_migrations LIMIT 1";
            await cmd.ExecuteScalarAsync(cancellationToken);
            return HealthCheckResult.Healthy();
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy(ex.Message, ex);
        }
    }
}
