namespace Relkit.Conformance;

public static class ConformanceSuite
{
    public static IReadOnlyList<ConformanceCase> All() =>
    [
        WorkedExamples.RoleGrantOverCategory(),               // 12.1
        WorkedExamples.TeamGrantOverCuratedSet(),             // 12.2
        WorkedExamples.SiteScopedAccess(),                    // 12.3
        WorkedExamples.SiteScopedAccessOtherSiteDenied(),     // 12.3 (negative)
        WorkedExamples.DirectGrantOnInstance(),               // 12.4
        WorkedExamples.QuarantineTrainedVetAllowed(),         // 12.5
        WorkedExamples.QuarantineUntrainedVetDenied(),        // 12.5 (negative)
        WorkedExamples.OutsideQuarantineBaseAccess(),         // 12.5 (passthrough)
        WorkedExamples.TimeBoundedDispenseWithinHours(),      // 12.6
    ];
}
