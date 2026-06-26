using Custodex.V1;

using Shouldly;

namespace Custodex.Client.Tests;

public sealed class ClientWiringTests
{
    [Fact]
    public void Generated_decision_client_type_exists()
    {
        var type = typeof(Decision.DecisionClient);
        type.ShouldNotBeNull();
    }

    [Fact]
    public void Generated_relations_client_type_exists()
    {
        var type = typeof(Relations.RelationsClient);
        type.ShouldNotBeNull();
    }

    [Fact]
    public void Generated_schema_client_type_exists()
    {
        var type = typeof(Schema.SchemaClient);
        type.ShouldNotBeNull();
    }

    [Fact]
    public void Generated_provisioning_client_type_exists()
    {
        var type = typeof(Provisioning.ProvisioningClient);
        type.ShouldNotBeNull();
    }
}
