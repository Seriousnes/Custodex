using System.Diagnostics;
using System.Diagnostics.Metrics;
using Custodex.Abstractions;
using Custodex.Core.Conditions;
using Custodex.Core.Evaluation;
using Custodex.Storage.InMemory;
using Shouldly;

namespace Custodex.Core.Tests.Evaluation;

public class CheckObservabilityTests
{
    private static readonly TenantContext T = new("zoo", "t1");

    private static async Task<EngineDrivenAuthorizer> NewAsync()
    {
        var schema = new SchemaBuilder("v1")
            .Type("doc", t => t
                .Relation("viewer", s => s.User())
                .Relation("editor", s => s.User())
                .Permission("access", p => p.Relation("viewer").Union(x => x.Relation("editor"))))
            .Build();
        var schemaStore = new InMemorySchemaStore();
        var relations = new InMemoryRelationStore();
        var attributes = new InMemoryAttributeStore();
        var uow = new NoOpUnitOfWork();
        await schemaStore.SetActiveAsync(T.Store, schema, uow);
        await relations.WriteAsync(T,
            new[] { new RelationTuple(new EntityRef("doc", "D1"), "editor", new SubjectRef("user", "alice")) },
            Array.Empty<RelationTuple>(), uow);
        await uow.CommitAsync();
        return new EngineDrivenAuthorizer(schemaStore, relations, attributes, new NullConditionEvaluator());
    }

    private static CheckRequest Req(bool explain) => new(
        T, new EntityRef("doc", "D1"), "access", new SubjectRef("user", "alice"),
        new RequestContext(DateTimeOffset.UnixEpoch, new SubjectRef("user", "alice"),
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
        explained.Explain!.Description.ShouldContain("access");
        // The union branch and its two relation children are present.
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
        conditionTouched.ShouldBeFalse();   // no conditions on this schema
    }
}
