using Shouldly;
using Xunit;

namespace Custodex.Abstractions.Tests;

public class DiagnosticsTests
{
    [Fact]
    public void Diagnostics_sources_are_named_Custodex()
    {
        CustodexDiagnostics.ActivitySource.Name.ShouldBe("Custodex");
        CustodexDiagnostics.Meter.Name.ShouldBe("Custodex");
    }
}
