using System.Reflection;

using Shouldly;

namespace Custodex.Client.Tests;

public sealed class ClientDependencyTests
{
    [Fact]
    public void Client_assembly_does_not_reference_the_engine()
    {
        var referenced = typeof(GrpcAuthorizer).Assembly
            .GetReferencedAssemblies()
            .Select(a => a.Name)
            .ToList();

        referenced.ShouldNotContain("Custodex.Core");
    }
}
