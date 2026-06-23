using Shouldly;
using Xunit;

namespace Relkit.Core.Tests;

public class CoreWiringTests
{
    [Fact]
    public void Core_references_abstractions()
    {
        typeof(Relkit.Core.SchemaBuilder).Assembly.GetName().Name.ShouldBe("Relkit.Core");
    }
}
