using Custodex.Api;

using Shouldly;

namespace Custodex.Service.Tests;

public sealed class ProtoCompileTests
{
    [Fact]
    public void CheckRequest_fields_round_trip_in_memory()
    {
        var req = new CheckRequest
        {
            Tenant = new TenantContext { Store = "s1", Tenant = "t1" },
            Object = new EntityRef { Type = "widget", Id = "42" },
            Permission = "view",
            Subject = new SubjectRef { Type = "user", Id = "alice" },
            Explain = false,
        };

        req.Tenant.Store.ShouldBe("s1");
        req.Tenant.Tenant.ShouldBe("t1");
        req.Object.Type.ShouldBe("widget");
        req.Object.Id.ShouldBe("42");
        req.Permission.ShouldBe("view");
        req.Subject.Type.ShouldBe("user");
        req.Subject.Id.ShouldBe("alice");
        req.Subject.Relation.ShouldBe(string.Empty);
    }

    [Fact]
    public void Decision_DecisionBase_type_exists()
    {
        var type = typeof(Decision.DecisionBase);
        type.ShouldNotBeNull();
    }
}
