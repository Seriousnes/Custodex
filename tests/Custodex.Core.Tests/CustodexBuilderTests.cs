using Microsoft.Extensions.DependencyInjection;
using Custodex.Core;
using Shouldly;

namespace Custodex.Core.Tests;

public class CustodexBuilderTests
{
    [Fact]
    public void AddCustodex_returns_a_builder_over_the_same_services()
    {
        var services = new ServiceCollection();
        var builder = services.AddCustodex();
        builder.Services.ShouldBeSameAs(services);
    }

    [Fact]
    public void UseSchema_captures_the_built_schema_for_startup_activation()
    {
        var services = new ServiceCollection();
        var builder = services.AddCustodex()
            .UseSchema(new SchemaBuilder("v1")
                .Type("doc", t => t.Relation("viewer", s => s.User()).Permission("view", p => p.Relation("viewer"))));
        builder.StartupSchema.ShouldNotBeNull();
        builder.StartupSchema!.Version.ShouldBe("v1");
    }
}
