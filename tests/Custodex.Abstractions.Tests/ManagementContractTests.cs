using Shouldly;
using Xunit;

namespace Custodex.Abstractions.Tests;

public class ManagementContractTests
{
    [Fact]
    public void ChangeLogFilter_defaults_limit_to_100()
    {
        new ChangeLogFilter().Limit.ShouldBe(100);
    }
}
