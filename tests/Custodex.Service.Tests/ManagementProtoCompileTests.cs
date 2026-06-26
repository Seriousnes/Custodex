using Custodex.V1;

using Shouldly;

namespace Custodex.Service.Tests;

public sealed class ManagementProtoCompileTests
{
    [Fact]
    public void Relations_RelationsBase_type_exists()
    {
        var type = typeof(Relations.RelationsBase);
        type.ShouldNotBeNull();
    }

    [Fact]
    public void Schema_SchemaBase_type_exists()
    {
        var type = typeof(Schema.SchemaBase);
        type.ShouldNotBeNull();
    }

    [Fact]
    public void Provisioning_ProvisioningBase_type_exists()
    {
        var type = typeof(Provisioning.ProvisioningBase);
        type.ShouldNotBeNull();
    }

    [Fact]
    public void RelationTuple_message_round_trips_with_subject_set()
    {
        var msg = new RelationTuple
        {
            Object = new EntityRef { Type = "widget", Id = "1" },
            Relation = "owner",
            Subject = new SubjectRef { Type = "group", Id = "grp-7", Relation = "member" },
        };

        msg.Object.Type.ShouldBe("widget");
        msg.Object.Id.ShouldBe("1");
        msg.Relation.ShouldBe("owner");
        msg.Subject.Type.ShouldBe("group");
        msg.Subject.Id.ShouldBe("grp-7");
        msg.Subject.Relation.ShouldBe("member");
    }
}
