using Shouldly;
using Xunit;

namespace Relkit.Abstractions.Tests;

public class WiringTests
{
    [Fact]
    public void Abstractions_assembly_is_referenced()
    {
        typeof(Relkit.Abstractions.EntityRef).Assembly.GetName().Name.ShouldBe("Relkit.Abstractions");
    }
}
