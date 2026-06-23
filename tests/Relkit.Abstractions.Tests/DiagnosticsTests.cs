using Shouldly;
using Xunit;

namespace Relkit.Abstractions.Tests;

public class DiagnosticsTests
{
    [Fact]
    public void Diagnostics_sources_are_named_relkit()
    {
        RelkitDiagnostics.ActivitySource.Name.ShouldBe("Relkit");
        RelkitDiagnostics.Meter.Name.ShouldBe("Relkit");
    }
}
