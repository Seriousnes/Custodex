using Custodex.Abstractions;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Custodex.Storage.SqlServer;

/// <summary>
/// Background TTL sweep for the <c>cache_entries</c> table. Runs <see cref="CacheSweep.RunAsync"/>
/// every <see cref="CacheSweepOptions.Interval"/> until the host stops, reporting reclaimed rows to
/// <see cref="CustodexDiagnostics.CacheSwept"/>. Opt-in: registered only when automatic sweeping is
/// wanted; the store stays correct without it (lazy expiry). It is dormant until cross-request
/// decision caching is wired, since nothing populates <c>cache_entries</c> by default.
/// </summary>
public sealed class CacheSweepService(CacheSweepOptions options, ILogger<CacheSweepService>? logger = null)
    : BackgroundService
{
    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.Interval);
        do
        {
            try
            {
                var reclaimed = await CacheSweep.RunAsync(options.ConnectionString, stoppingToken);
                if (reclaimed > 0)
                    CustodexDiagnostics.CacheSwept.Add(reclaimed);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "Cache TTL sweep failed; retrying on the next interval.");
            }
        }
        while (await SafeWaitAsync(timer, stoppingToken));
    }

    private static async Task<bool> SafeWaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try { return await timer.WaitForNextTickAsync(ct); }
        catch (OperationCanceledException) { return false; }
    }
}
