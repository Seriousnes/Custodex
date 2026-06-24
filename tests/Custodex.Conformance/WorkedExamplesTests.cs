namespace Custodex.Conformance;

public class WorkedExamplesTests
{
    public static IEnumerable<object[]> Cases() =>
    [
        [WorkedExamples.RoleGrantOverCategory()],
        [WorkedExamples.TeamGrantOverCuratedSet()],
        [WorkedExamples.SiteScopedAccess()],
        [WorkedExamples.SiteScopedAccessOtherSiteDenied()],
        [WorkedExamples.DirectGrantOnInstance()],
    ];

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Worked_example_holds(ConformanceCase c) => await ConformanceRunner.AssertAsync(c);
}
