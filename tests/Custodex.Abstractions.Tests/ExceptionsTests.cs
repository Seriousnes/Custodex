using Shouldly;

namespace Custodex.Abstractions.Tests;

public class ExceptionsTests
{
    [Fact]
    public void UnknownTypeException_carries_the_type()
    {
        var ex = new UnknownTypeException("dragon");
        ex.Message.ShouldContain("dragon");
    }
}
