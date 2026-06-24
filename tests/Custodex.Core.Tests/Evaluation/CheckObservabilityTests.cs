using System.Diagnostics;
using System.Diagnostics.Metrics;
using Custodex.Abstractions;
using Custodex.Core.Evaluation;
using Custodex.TestKit;
using Shouldly;

namespace Custodex.Core.Tests.Evaluation;

public class CheckObservabilityTests
{
    private readonly TestWorld _world = TestWorld.New();
    private readonly string _objType;
    private readonly string _viewer;
    private readonly string _editor;
    private readonly string _access;
    private readonly string _objId;
    private readonly string _subjectId;

    public CheckObservabilityTests()
    {
        _objType = _world.EntityType();
        _viewer = _world.Relation();
        _editor = _world.Relation();
        _access = _world.Permission();
        _objId = _world.ObjectId();
        _subjectId = _world.SubjectId();
    }

    private Task<EngineDrivenAuthorizer> NewAsync()
    {
        var schema = new SchemaBuilder(_world.Version)
            .Type(_objType, t => t
                .Relation(_viewer, s => s.Type(_world.UserType))
                .Relation(_editor, s => s.Type(_world.UserType))
                .Permission(_access, p => p.Relation(_viewer).Union(x => x.Relation(_editor))))
            .Build();
        return _world.BuildAsync(schema,
            _world.Tuple(_objType, _objId, _editor, _world.User(_subjectId)));
    }

    private CheckRequest Req(bool explain) => new(
        _world.Tenant, new EntityRef(_objType, _objId), _access, _world.User(_subjectId),
        new RequestContext(DateTimeOffset.UnixEpoch, _world.User(_subjectId),
            new Dictionary<string, object?>()), Explain: explain);

    [Fact]
    public async Task Explain_tree_is_populated_when_requested_and_null_otherwise()
    {
        var auth = await NewAsync();
        var plain = await auth.CheckAsync(Req(explain: false));
        plain.Allowed.ShouldBeTrue();
        plain.Explain.ShouldBeNull();

        var explained = await auth.CheckAsync(Req(explain: true));
        explained.Allowed.ShouldBeTrue();
        explained.Explain.ShouldNotBeNull();
        explained.Explain!.Description.ShouldContain(_access);
        explained.Explain.Children.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task Check_emits_an_activity_span()
    {
        var captured = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = src => src.Name == "Custodex",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = captured.Add
        };
        ActivitySource.AddActivityListener(listener);

        var auth = await NewAsync();
        await auth.CheckAsync(Req(explain: false));

        captured.ShouldContain(a => a.OperationName == "Custodex.check");
    }

    [Fact]
    public async Task Check_records_duration_histogram()
    {
        var measured = false;
        using var mlistener = new MeterListener();
        mlistener.InstrumentPublished = (inst, l) =>
        {
            if (inst.Meter.Name == "Custodex" && inst.Name == "Custodex.check.duration")
                l.EnableMeasurementEvents(inst);
        };
        mlistener.SetMeasurementEventCallback<double>((_, _, _, _) => measured = true);
        mlistener.Start();

        var auth = await NewAsync();
        await auth.CheckAsync(Req(explain: false));

        measured.ShouldBeTrue();
    }

    [Fact]
    public async Task Internal_check_reports_condition_touched_flag()
    {
        var auth = await NewAsync();
        var (allowed, conditionTouched) = await auth.CheckInternalAsync(Req(explain: false));
        allowed.ShouldBeTrue();
        conditionTouched.ShouldBeFalse();
    }
}
