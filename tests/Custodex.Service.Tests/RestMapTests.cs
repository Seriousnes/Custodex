using Custodex.Abstractions;
using Custodex.Service.Rest;
using Shouldly;

namespace Custodex.Service.Tests;

public sealed class RestMapTests
{
    [Fact]
    public void EntityRefDto_wildcard_round_trips()
    {
        var dto = new EntityRefDto("widget", "*");
        var record = RestMap.FromDto(dto);
        var back = RestMap.ToDto(record);

        record.IsWildcard.ShouldBeTrue();
        back.ShouldBe(dto);
    }

    [Fact]
    public void SubjectRefDto_plain_subject_is_not_subject_set()
    {
        var dto = new SubjectRefDto("user", "u-1", null);
        var record = RestMap.FromDto(dto);

        record.IsSubjectSet.ShouldBeFalse();
        record.Relation.ShouldBeNull();
    }

    [Fact]
    public void SubjectRefDto_with_relation_is_subject_set()
    {
        var dto = new SubjectRefDto("grp", "g-1", "member");
        var record = RestMap.FromDto(dto);

        record.IsSubjectSet.ShouldBeTrue();
        record.Relation.ShouldBe("member");
    }

    [Fact]
    public void RequestContextDto_null_Now_defaults_and_attributes_map()
    {
        var dto = new RequestContextDto(
            Subject: new SubjectRefDto("user", "u-2", null),
            Now: null,
            Attributes: new Dictionary<string, object?> { ["level"] = 3.0 });

        var ctx = RestMap.FromDto(dto);

        ctx.Subject.Type.ShouldBe("user");
        ctx.Now.ShouldNotBe(default);
        ctx.Attributes.ShouldContainKey("level");
    }

    [Fact]
    public void ExplainNode_with_child_maps_recursively()
    {
        var node = new ExplainNode(
            Description: "owner",
            Allowed: true,
            Children: [new ExplainNode("direct", true, [])]);

        var dto = RestMap.ToDto(node);

        dto.Description.ShouldBe("owner");
        dto.Allowed.ShouldBeTrue();
        dto.Children.ShouldNotBeEmpty();
        dto.Children[0].Description.ShouldBe("direct");
    }

    [Fact]
    public void Tenant_helper_produces_correct_TenantContext()
    {
        var ctx = RestMap.Tenant("my-store", "t-1");
        ctx.Store.ShouldBe("my-store");
        ctx.Tenant.ShouldBe("t-1");
    }
}
