using System.Diagnostics.Metrics;

using Custodex.Abstractions;
using Custodex.TestKit;

using Shouldly;

namespace Custodex.Core.Tests.Metrics;

[Collection("check duration meter")]
public sealed class MeteredAuthorizerTests
{
    private sealed class StubAuthorizer(Func<CheckResult> check) : IAuthorizer
    {
        public Task<CheckResult> CheckAsync(CheckRequest request, CancellationToken ct = default) =>
            Task.FromResult(check());

        public Task<IReadOnlyList<CheckResult>> BatchCheckAsync(BatchCheckRequest request, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<CheckResult>>([.. request.Items.Select(_ => check())]);

        public Task<ListObjectsResult> ListObjectsAsync(ListObjectsRequest request, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<ListSubjectsResult> ListSubjectsAsync(ListSubjectsRequest request, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private static MeterListener ListenForCheckDuration(Action onMeasurement)
    {
        var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == CustodexDiagnostics.Name && instrument.Name == "Custodex.check.duration")
                    l.EnableMeasurementEvents(instrument);
            },
        };
        listener.SetMeasurementEventCallback<double>((_, _, _, _) => onMeasurement());
        listener.Start();
        return listener;
    }

    [Fact]
    public async Task Check_through_the_decorator_records_one_duration_and_returns_the_inner_result()
    {
        var world = TestWorld.New();
        var metered = new MeteredAuthorizer(new StubAuthorizer(() => new CheckResult(true)));
        var recorded = 0;
        using var listener = ListenForCheckDuration(() => recorded++);

        var result = await metered.CheckAsync(
            world.Check(world.EntityType(), world.ObjectId(), world.Permission(), world.SubjectId()));

        result.Allowed.ShouldBeTrue();
        recorded.ShouldBe(1);
    }

    [Fact]
    public async Task Check_through_the_decorator_records_a_duration_when_the_inner_check_throws()
    {
        var world = TestWorld.New();
        var metered = new MeteredAuthorizer(new StubAuthorizer(() => throw new InvalidOperationException()));
        var recorded = 0;
        using var listener = ListenForCheckDuration(() => recorded++);

        await Should.ThrowAsync<InvalidOperationException>(() => metered.CheckAsync(
            world.Check(world.EntityType(), world.ObjectId(), world.Permission(), world.SubjectId())));

        recorded.ShouldBe(1);
    }

    [Fact]
    public async Task BatchCheck_through_the_decorator_records_no_duration()
    {
        var world = TestWorld.New();
        var metered = new MeteredAuthorizer(new StubAuthorizer(() => new CheckResult(true)));
        var recorded = 0;
        using var listener = ListenForCheckDuration(() => recorded++);

        var check = world.Check(world.EntityType(), world.ObjectId(), world.Permission(), world.SubjectId());
        var results = await metered.BatchCheckAsync(new BatchCheckRequest(
            world.Tenant, [new CheckItem(check.Object, check.Permission, check.Subject)], check.Context));

        results.ShouldHaveSingleItem().Allowed.ShouldBeTrue();
        recorded.ShouldBe(0);
    }
}
