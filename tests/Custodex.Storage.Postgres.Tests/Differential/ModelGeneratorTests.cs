using CsCheck;

using Custodex.Core.Validation;

namespace Custodex.Storage.Postgres.Tests.Differential;

/// <summary>Property tests that guard the model generator's structural invariants.</summary>
public class ModelGeneratorTests
{
    /// <summary>Every schema the generator produces must pass structural validation so both authorizers
    /// run real evaluations rather than throwing in lockstep.</summary>
    [Fact]
    public void Every_generated_schema_passes_validation()
    {
        Check.Sample(ModelGenerator.Gen, model =>
        {
            var result = SchemaValidator.Validate(model.Schema);
            return result.IsValid;
        }, iter: 200);
    }

    /// <summary>The generator must always produce at least one probe object and one probe subject
    /// so the harness has something to Check against.</summary>
    [Fact]
    public void Generator_produces_probe_objects_and_subjects()
    {
        Check.Sample(ModelGenerator.Gen,
            model => model.ProbeObjects.Count > 0 && model.ProbeSubjects.Count > 0,
            iter: 50);
    }
}
