using Shouldly;
using Xunit;

namespace Custodex.Conformance;

public class SuiteCoverageTests
{
    [Fact]
    public void Suite_covers_all_six_worked_examples()
    {
        var names = ConformanceSuite.All().Select(c => c.Name).ToList();
        foreach (var prefix in new[] { "12.1", "12.2", "12.3", "12.4", "12.5", "12.6" })
            names.ShouldContain(n => n.StartsWith(prefix), $"no conformance case for worked example {prefix}");
    }

    [Fact]
    public async Task Every_case_in_the_suite_holds()
    {
        foreach (var c in ConformanceSuite.All())
            await ConformanceRunner.AssertAsync(c);
    }
}
