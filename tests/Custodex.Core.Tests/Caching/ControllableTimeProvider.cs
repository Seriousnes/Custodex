namespace Custodex.Core.Tests.Caching;

internal sealed class ControllableTimeProvider(DateTimeOffset start) : TimeProvider
{
    private long _utcTicks = start.UtcTicks;
    private long _timestamp = start.UtcTicks;

    public override DateTimeOffset GetUtcNow() => new(Volatile.Read(ref _utcTicks), TimeSpan.Zero);

    public override long GetTimestamp() => Volatile.Read(ref _timestamp);

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

    public void SetUtcNow(DateTimeOffset value) => Volatile.Write(ref _utcTicks, value.UtcTicks);

    public void AdvanceTimestamp(TimeSpan delta) => Interlocked.Add(ref _timestamp, delta.Ticks);
}
