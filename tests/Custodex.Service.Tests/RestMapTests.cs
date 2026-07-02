using Custodex.Abstractions;
using Custodex.Service.Rest;
using Custodex.TestKit;

using Shouldly;

namespace Custodex.Service.Tests;

public sealed class RestMapTests
{
    [Fact]
    public void CheckResult_allow_and_deny_map_stable_decision_strings()
    {
        var allow = RestMap.ToDto(new CheckResult(true));
        var deny = RestMap.ToDto(new CheckResult(false));

        allow.Decision.ShouldBe("allow");
        allow.Allowed.ShouldBeTrue();
        allow.UnmetConditions.ShouldBeEmpty();
        deny.Decision.ShouldBe("deny");
        deny.Allowed.ShouldBeFalse();
        deny.UnmetConditions.ShouldBeEmpty();
    }

    [Fact]
    public void CheckResult_conditional_maps_decision_and_unmet_payload()
    {
        var world = TestWorld.New();
        var condition = world.ConditionName();
        var key = world.ParamName();
        var result = new CheckResult(CheckDecision.Conditional, [new UnmetCondition(condition, [key])]);

        var dto = RestMap.ToDto(result);

        dto.Allowed.ShouldBeFalse();
        dto.Decision.ShouldBe("conditional");
        dto.UnmetConditions.Count.ShouldBe(1);
        dto.UnmetConditions[0].Condition.ShouldBe(condition);
        dto.UnmetConditions[0].MissingKeys.ShouldBe([key]);
    }

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
    public void RequestContextDto_maps_at_least_as_fresh_consistency()
    {
        var token = ConsistencyToken.Create(TestWorld.New().Tenant, epoch: 3, changeLogId: 4);
        var dto = new RequestContextDto(
            Subject: new SubjectRefDto("user", "u-3", null),
            Now: null,
            Attributes: null,
            Consistency: new ConsistencyDto("at-least-as-fresh", token.Value));

        var ctx = RestMap.FromDto(dto);

        ctx.Consistency!.Mode.ShouldBe(ConsistencyMode.AtLeastAsFresh);
        ctx.Consistency.Token!.Value.ShouldBe(token.Value);
    }

    [Fact]
    public void RequestContextDto_maps_fully_consistent()
    {
        var dto = new RequestContextDto(
            Subject: new SubjectRefDto("user", "u-4", null),
            Now: null,
            Attributes: null,
            Consistency: new ConsistencyDto("fully-consistent"));

        RestMap.FromDto(dto).Consistency!.Mode.ShouldBe(ConsistencyMode.FullyConsistent);
    }

    [Fact]
    public void RequestContextDto_absent_consistency_is_null()
    {
        var dto = new RequestContextDto(
            Subject: new SubjectRefDto("user", "u-5", null),
            Now: null,
            Attributes: null);

        RestMap.FromDto(dto).Consistency.ShouldBeNull();
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
}
