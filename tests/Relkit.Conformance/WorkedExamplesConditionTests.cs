using Relkit.Core.Conditions;
using Xunit;

namespace Relkit.Conformance;

public class WorkedExamplesConditionTests
{
    public static IEnumerable<object[]> Cases() =>
    [
        [WorkedExamples.TimeBoundedDispenseRealInHours()],
        [WorkedExamples.TimeBoundedDispenseRealOutOfHours()],
    ];

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Time_gating_holds_under_the_real_evaluator(ConformanceCase c) =>
        await ConformanceRunner.AssertAsync(c, new CelConditionEvaluator());
}
