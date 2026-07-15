using System.Diagnostics.Metrics;

using Custodex.Core;

using Shouldly;

namespace Custodex.Core.Tests.Metrics;

public sealed class CustodexMeterAggregatorTests
{
    private sealed class FakeTimeProvider : TimeProvider
    {
        private DateTimeOffset _now;

        public FakeTimeProvider(DateTimeOffset start) => _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan delta) => _now += delta;
    }

    private sealed class MeterRig : IDisposable
    {
        public Meter Meter { get; }
        public Histogram<double> CheckDuration { get; }
        public Counter<long> CacheHits { get; }
        public Counter<long> CacheMisses { get; }
        public Counter<long> CacheSwept { get; }

        public MeterRig(string meterName)
        {
            Meter = new Meter(meterName);
            CheckDuration = Meter.CreateHistogram<double>("Custodex.check.duration", unit: "ms");
            CacheHits = Meter.CreateCounter<long>("Custodex.cache.hits");
            CacheMisses = Meter.CreateCounter<long>("Custodex.cache.misses");
            CacheSwept = Meter.CreateCounter<long>("Custodex.cache.swept");
        }

        public void Dispose() => Meter.Dispose();
    }

    private static string UniqueMeterName() => $"test-meter-{Guid.NewGuid():N}";

    [Fact]
    public async Task Capture_reports_count_and_nearest_rank_percentiles_over_recorded_latencies()
    {
        var meterName = UniqueMeterName();
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        using var rig = new MeterRig(meterName);
        using var aggregator = new CustodexMeterAggregator(time, meterName);
        await aggregator.StartAsync(CancellationToken.None);

        for (var value = 1; value <= 100; value++)
            rig.CheckDuration.Record(value);

        var snapshot = await aggregator.CaptureAsync();
        await aggregator.StopAsync(CancellationToken.None);

        snapshot.CheckCount.ShouldBe(100);
        snapshot.CheckP50Ms.ShouldBe(50);
        snapshot.CheckP95Ms.ShouldBe(95);
        snapshot.CheckP99Ms.ShouldBe(99);
        snapshot.CapturedAt.ShouldBe(DateTimeOffset.UnixEpoch);
    }

    [Fact]
    public async Task Capture_accumulates_cache_counter_totals()
    {
        var meterName = UniqueMeterName();
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        using var rig = new MeterRig(meterName);
        using var aggregator = new CustodexMeterAggregator(time, meterName);
        await aggregator.StartAsync(CancellationToken.None);

        rig.CacheHits.Add(3);
        rig.CacheHits.Add(2);
        rig.CacheMisses.Add(7);
        rig.CacheSwept.Add(4);

        var snapshot = await aggregator.CaptureAsync();
        await aggregator.StopAsync(CancellationToken.None);

        snapshot.CacheHits.ShouldBe(5);
        snapshot.CacheMisses.ShouldBe(7);
        snapshot.CacheSwept.ShouldBe(4);
    }

    [Fact]
    public async Task Capture_prunes_latencies_older_than_the_rolling_window()
    {
        var meterName = UniqueMeterName();
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        using var rig = new MeterRig(meterName);
        using var aggregator = new CustodexMeterAggregator(time, meterName);
        await aggregator.StartAsync(CancellationToken.None);

        for (var i = 0; i < 10; i++)
            rig.CheckDuration.Record(1000);

        time.Advance(TimeSpan.FromMinutes(6));

        for (var i = 0; i < 5; i++)
            rig.CheckDuration.Record(2);

        var snapshot = await aggregator.CaptureAsync();
        await aggregator.StopAsync(CancellationToken.None);

        snapshot.CheckCount.ShouldBe(5);
        snapshot.CheckP50Ms.ShouldBe(2);
        snapshot.CheckP95Ms.ShouldBe(2);
        snapshot.CheckP99Ms.ShouldBe(2);
    }

    [Fact]
    public async Task Capture_on_empty_window_yields_zero_count_and_zero_percentiles()
    {
        var meterName = UniqueMeterName();
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        using var rig = new MeterRig(meterName);
        using var aggregator = new CustodexMeterAggregator(time, meterName);
        await aggregator.StartAsync(CancellationToken.None);

        var snapshot = await aggregator.CaptureAsync();
        await aggregator.StopAsync(CancellationToken.None);

        snapshot.CheckCount.ShouldBe(0);
        snapshot.CheckP50Ms.ShouldBe(0);
        snapshot.CheckP95Ms.ShouldBe(0);
        snapshot.CheckP99Ms.ShouldBe(0);
        snapshot.CacheHits.ShouldBe(0);
        snapshot.CacheMisses.ShouldBe(0);
        snapshot.CacheSwept.ShouldBe(0);
    }
}
