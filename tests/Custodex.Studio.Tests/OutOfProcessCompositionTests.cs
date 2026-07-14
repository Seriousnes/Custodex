using Custodex.Abstractions;
using Custodex.Client;
using Custodex.Studio;
using Custodex.Studio.Views;

using Microsoft.Extensions.DependencyInjection;

using Shouldly;

namespace Custodex.Studio.Tests;

public sealed class OutOfProcessCompositionTests
{
    [Fact]
    public void Client_plus_studio_resolves_the_full_console_dependency_set()
    {
        var services = new ServiceCollection();
        services.AddCustodexClient("http://localhost:9999");
        services.AddCustodexStudio();

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var sp = scope.ServiceProvider;

        sp.GetRequiredService<IRelationManager>().ShouldNotBeNull();
        sp.GetRequiredService<ISchemaManager>().ShouldNotBeNull();
        sp.GetRequiredService<IAuthorizer>().ShouldNotBeNull();
        sp.GetRequiredService<IMetricsSnapshotProvider>().ShouldNotBeNull();
        sp.GetRequiredService<IStudioViewStore>().ShouldBeOfType<InMemoryStudioViewStore>();
        sp.GetRequiredService<TimeProvider>().ShouldBe(TimeProvider.System);
        sp.GetRequiredService<StudioConnectionState>().ShouldNotBeNull();
    }
}
