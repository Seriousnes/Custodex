using Xunit;

namespace Custodex.Conformance;

public class WorkedExamplesGateTests
{
    public static IEnumerable<object[]> Cases() =>
    [
        [WorkedExamples.QuarantineTrainedVetAllowed()],
        [WorkedExamples.QuarantineUntrainedVetDenied()],
        [WorkedExamples.OutsideQuarantineBaseAccess()],
        [WorkedExamples.TimeBoundedDispenseWithinHours()],
    ];

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Gate_and_condition_examples_hold(ConformanceCase c) => await ConformanceRunner.AssertAsync(c);
}
