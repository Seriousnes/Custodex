using Shouldly;

namespace Custodex.Abstractions.Tests;

public class WiringTests
{
    [Fact]
    public void Abstractions_assembly_is_referenced()
    {
        typeof(Custodex.Abstractions.EntityRef).Assembly.GetName().Name.ShouldBe("Custodex.Abstractions");
    }
}
