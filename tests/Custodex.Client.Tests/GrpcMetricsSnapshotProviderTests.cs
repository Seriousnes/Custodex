using Grpc.Core;

using Shouldly;

namespace Custodex.Client.Tests;

public sealed class GrpcMetricsSnapshotProviderTests
{
    [Fact]
    public async Task Capture_returns_the_served_snapshot()
    {
        var captured = new DateTimeOffset(2026, 7, 14, 9, 15, 0, TimeSpan.Zero);
        var served = new Custodex.Api.MetricsSnapshot
        {
            CapturedAt = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(captured),
            CheckCount = 7,
            CheckP50Ms = 1.5,
            CheckP95Ms = 2.5,
            CheckP99Ms = 3.5,
            CacheHits = 11,
            CacheMisses = 22,
            CacheSwept = 33,
        };

        var provider = new GrpcMetricsSnapshotProvider(new FakeMetricsClient(served));
        var snapshot = await provider.CaptureAsync();

        snapshot.CapturedAt.ShouldBe(captured);
        snapshot.CheckCount.ShouldBe(7);
        snapshot.CheckP50Ms.ShouldBe(1.5);
        snapshot.CheckP95Ms.ShouldBe(2.5);
        snapshot.CheckP99Ms.ShouldBe(3.5);
        snapshot.CacheHits.ShouldBe(11);
        snapshot.CacheMisses.ShouldBe(22);
        snapshot.CacheSwept.ShouldBe(33);
    }

    private sealed class FakeMetricsClient(Custodex.Api.MetricsSnapshot response) : Custodex.Api.Metrics.MetricsClient
    {
        private readonly Custodex.Api.MetricsSnapshot _response = response;

        public override AsyncUnaryCall<Custodex.Api.MetricsSnapshot> GetSnapshotAsync(
            Custodex.Api.GetMetricsSnapshotRequest request,
            CallOptions options) =>
            new(
                Task.FromResult(_response),
                Task.FromResult(new Metadata()),
                () => Status.DefaultSuccess,
                () => [],
                () => { });
    }
}
