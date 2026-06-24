using Shouldly;

namespace Custodex.Core.Tests;

public class CoreWiringTests
{
    [Fact]
    public void Core_references_abstractions()
    {
        typeof(Custodex.Core.SchemaBuilder).Assembly.GetName().Name.ShouldBe("Custodex.Core");
    }
}
