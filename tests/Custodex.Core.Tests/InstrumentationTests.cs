using System.Diagnostics;

using Custodex.Abstractions;

using OpenTelemetry;
using OpenTelemetry.Trace;

using Shouldly;

namespace Custodex.Core.Tests;

public class InstrumentationTests
{
    [Fact]
    public void AddCustodexInstrumentation_subscribes_the_Custodex_activity_source()
    {
        var exported = new List<Activity>();
        using var tracer = Sdk.CreateTracerProviderBuilder()
            .AddCustodexInstrumentation()
            .AddInMemoryExporter(exported)
            .Build();

        using (var activity = CustodexDiagnostics.ActivitySource.StartActivity("Custodex.check"))
            activity?.SetTag("test", "1");

        exported.ShouldContain(a => a.DisplayName == "Custodex.check");
    }
}
