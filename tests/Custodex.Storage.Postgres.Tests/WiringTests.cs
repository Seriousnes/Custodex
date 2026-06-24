using Shouldly;

namespace Custodex.Storage.Postgres.Tests;

public class WiringTests
{
    [Fact]
    public void Storage_assembly_is_referenced()
    {
        typeof(MigrationRunner).Assembly.GetName().Name
            .ShouldBe("Custodex.Storage.Postgres");
    }
}
