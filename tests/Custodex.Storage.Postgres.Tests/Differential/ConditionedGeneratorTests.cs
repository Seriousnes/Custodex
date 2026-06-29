using CsCheck;

using Custodex.Abstractions;
using Custodex.Core.Validation;

using Shouldly;

namespace Custodex.Storage.Postgres.Tests.Differential;

/// <summary>Property tests guarding that the conditioned generator emits the shapes the differential
/// harness needs to catch a condition-path divergence.</summary>
public class ConditionedGeneratorTests
{
    [Fact]
    public void Every_conditioned_schema_passes_validation()
    {
        Check.Sample(ModelGenerator.ConditionedGen,
            model => SchemaValidator.Validate(model.Schema).IsValid, iter: 200);
    }

    [Fact]
    public void Generator_emits_conditioned_tuples()
    {
        var sawConditioned = false;
        Check.Sample(ModelGenerator.ConditionedGen, model =>
        {
            if (model.Tuples.Any(t => t.Condition is not null)) sawConditioned = true;
            return true;
        }, iter: 200);
        sawConditioned.ShouldBeTrue();
    }

    [Fact]
    public void Generator_emits_conditioned_tuples_reached_through_subject_sets()
    {
        var sawDeepConditioned = false;
        Check.Sample(ModelGenerator.ConditionedGen, model =>
        {
            if (model.Tuples.Any(t => t.Condition is not null && t.Subject.Type == "user")
                && model.Tuples.Any(t => t.Subject.Relation is not null))
                sawDeepConditioned = true;
            return true;
        }, iter: 200);
        sawDeepConditioned.ShouldBeTrue();
    }

    [Fact]
    public void Generator_emits_object_attributes()
    {
        var sawAttributes = false;
        Check.Sample(ModelGenerator.ConditionedGen, model =>
        {
            if (model.Attributes.Count > 0) sawAttributes = true;
            return true;
        }, iter: 50);
        sawAttributes.ShouldBeTrue();
    }

    [Fact]
    public void Generator_emits_timestamp_conditions_and_attributes()
    {
        var sawTimestamp = false;
        Check.Sample(ModelGenerator.ConditionedGen, model =>
        {
            var hasTimestampParam = model.Schema.Conditions
                .Any(c => c.Parameters.Any(p => p.Type == ConditionType.Timestamp));
            var hasTimestampAttribute = model.Attributes
                .Any(a => a.Attrs.Values.Any(v => v is DateTimeOffset));
            if (hasTimestampParam && hasTimestampAttribute) sawTimestamp = true;
            return true;
        }, iter: 200);
        sawTimestamp.ShouldBeTrue();
    }
}
