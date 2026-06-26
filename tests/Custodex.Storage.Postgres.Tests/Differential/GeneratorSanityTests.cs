using CsCheck;

using Shouldly;

namespace Custodex.Storage.Postgres.Tests.Differential;

public class GeneratorSanityTests
{
    [Fact]
    public void Write_sequences_include_exclusion_tuples()
    {
        var sawBlocked = false;
        Check.Sample(IndexModelGenerators.WriteSequence, ops =>
        {
            if (ops.Any(o => o.Tuple.Relation == "blocked")) sawBlocked = true;
            return true;
        }, iter: 200);
        sawBlocked.ShouldBeTrue();
    }
}
