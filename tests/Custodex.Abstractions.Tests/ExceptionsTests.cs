using Custodex.TestKit;
using Shouldly;

namespace Custodex.Abstractions.Tests;

public class ExceptionsTests
{
    [Fact]
    public void UnknownTypeException_carries_the_type()
    {
        var world = TestWorld.New();
        var type = world.EntityType();
        var ex = new UnknownTypeException(type);
        ex.Message.ShouldContain(type);
    }
}
