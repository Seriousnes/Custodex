using System.Diagnostics.Metrics;

using Custodex.Abstractions;

using Microsoft.Extensions.Hosting;

namespace Custodex.Core;

/// <summary>
/// Listens in-process to the engine's <c>"Custodex"</c> meter and aggregates it into a pollable
/// <see cref="MetricsSnapshot"/>. Check-latency samples are kept in a time-bounded rolling window and
/// summarised as nearest-rank percentiles; cache instruments accumulate as cumulative totals. The
/// listener is started and stopped with the host and is safe to call from arbitrary recording threads.
/// </summary>
public sealed class CustodexMeterAggregator : IMetricsSnapshotProvider, IHostedService, IDisposable
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(5);
    private const int MaxSamples = 50_000;

    private readonly TimeProvider _time;
    private readonly string _meterName;
    private readonly object _gate = new();
    private readonly Queue<Sample> _window = new();

    private long _cacheHits;
    private long _cacheMisses;
    private long _cacheSwept;

    private MeterListener? _listener;
    private bool _disposed;

    /// <summary>Creates the aggregator. Recording does not begin until <see cref="StartAsync"/> runs.</summary>
    /// <param name="time">The time source the rolling window is measured against.</param>
    /// <param name="meterName">The meter name to listen to; defaults to <see cref="CustodexDiagnostics.Name"/>.</param>
    public CustodexMeterAggregator(TimeProvider time, string meterName = CustodexDiagnostics.Name)
    {
        _time = time;
        _meterName = meterName;
    }

    private readonly record struct Sample(DateTimeOffset At, double ValueMs);

    /// <inheritdoc/>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_disposed || _listener is not null)
                return Task.CompletedTask;

            var meterName = _meterName;
            var listener = new MeterListener
            {
                InstrumentPublished = (instrument, l) =>
                {
                    if (instrument.Meter.Name == meterName)
                        l.EnableMeasurementEvents(instrument);
                },
            };
            listener.SetMeasurementEventCallback<double>(OnDoubleRecorded);
            listener.SetMeasurementEventCallback<long>(OnLongRecorded);
            listener.Start();
            _listener = listener;
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task StopAsync(CancellationToken cancellationToken)
    {
        DisposeListener();
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<MetricsSnapshot> CaptureAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(Capture());
    }

    private MetricsSnapshot Capture()
    {
        var now = _time.GetUtcNow();
        lock (_gate)
        {
            Prune(now);

            var count = _window.Count;
            double p50 = 0, p95 = 0, p99 = 0;
            if (count > 0)
            {
                var values = new double[count];
                var i = 0;
                foreach (var sample in _window)
                    values[i++] = sample.ValueMs;
                Array.Sort(values);
                p50 = Percentile(values, 50);
                p95 = Percentile(values, 95);
                p99 = Percentile(values, 99);
            }

            return new MetricsSnapshot(now, count, p50, p95, p99, _cacheHits, _cacheMisses, _cacheSwept);
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        lock (_gate)
            _disposed = true;
        DisposeListener();
    }

    private void OnDoubleRecorded(Instrument instrument, double measurement, ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state)
    {
        if (instrument.Name != "Custodex.check.duration")
            return;

        var now = _time.GetUtcNow();
        lock (_gate)
        {
            _window.Enqueue(new Sample(now, measurement));
            Prune(now);
            while (_window.Count > MaxSamples)
                _window.Dequeue();
        }
    }

    private void OnLongRecorded(Instrument instrument, long measurement, ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state)
    {
        lock (_gate)
        {
            switch (instrument.Name)
            {
                case "Custodex.cache.hits":
                    _cacheHits += measurement;
                    break;
                case "Custodex.cache.misses":
                    _cacheMisses += measurement;
                    break;
                case "Custodex.cache.swept":
                    _cacheSwept += measurement;
                    break;
            }
        }
    }

    private void Prune(DateTimeOffset now)
    {
        var cutoff = now - Window;
        while (_window.Count > 0 && _window.Peek().At < cutoff)
            _window.Dequeue();
    }

    private static double Percentile(double[] sortedAscending, double percentile)
    {
        var rank = (int)Math.Ceiling(percentile / 100d * sortedAscending.Length);
        var index = Math.Clamp(rank - 1, 0, sortedAscending.Length - 1);
        return sortedAscending[index];
    }

    private void DisposeListener()
    {
        MeterListener? toDispose;
        lock (_gate)
        {
            toDispose = _listener;
            _listener = null;
        }

        toDispose?.Dispose();
    }
}
