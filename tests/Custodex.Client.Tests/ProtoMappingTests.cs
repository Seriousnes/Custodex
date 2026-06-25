extern alias ClientProtos;

using ClientProtos::Custodex.Client.Mapping;
using Custodex.Abstractions;
using Shouldly;

namespace Custodex.Client.Tests;

public sealed class ProtoMappingTests
{
    [Fact]
    public void EntityRef_plain_round_trips()
    {
        var domain = new EntityRef("doc", "d-1");
        ProtoMapping.ToDomain(ProtoMapping.ToProto(domain)).ShouldBe(domain);
    }

    [Fact]
    public void EntityRef_wildcard_round_trips()
    {
        var domain = new EntityRef("doc", "*");
        ProtoMapping.ToDomain(ProtoMapping.ToProto(domain)).ShouldBe(domain);
    }

    [Fact]
    public void SubjectRef_plain_round_trips_and_is_not_subject_set()
    {
        var domain = new SubjectRef("user", "u-1", null);
        var result = ProtoMapping.ToDomain(ProtoMapping.ToProto(domain));
        result.ShouldBe(domain);
        result.IsSubjectSet.ShouldBeFalse();
    }

    [Fact]
    public void SubjectRef_subject_set_round_trips_and_is_subject_set()
    {
        var domain = new SubjectRef("group", "g-1", "member");
        var result = ProtoMapping.ToDomain(ProtoMapping.ToProto(domain));
        result.ShouldBe(domain);
        result.IsSubjectSet.ShouldBeTrue();
    }

    [Fact]
    public void Tuple_with_condition_round_trips_with_typed_params()
    {
        var domain = new RelationTuple(
            new EntityRef("doc", "d-1"),
            "owner",
            new SubjectRef("user", "u-1", null),
            new ConditionRef("is_premium", new Dictionary<string, object?> { ["level"] = 5L }));
        var result = ProtoMapping.ToDomain(ProtoMapping.ToProto(domain));
        result.Object.ShouldBe(domain.Object);
        result.Relation.ShouldBe(domain.Relation);
        result.Subject.ShouldBe(domain.Subject);
        result.Condition.ShouldNotBeNull();
        result.Condition!.Name.ShouldBe("is_premium");
        result.Condition.Parameters["level"].ShouldBe(5L);
    }

    [Fact]
    public void Unconditioned_tuple_has_null_condition()
    {
        var domain = new RelationTuple(
            new EntityRef("doc", "d-1"), "owner", new SubjectRef("user", "u-1", null), null);
        ProtoMapping.ToDomain(ProtoMapping.ToProto(domain)).Condition.ShouldBeNull();
    }

    [Fact]
    public void Attribute_struct_typed_values_round_trip()
    {
        var attrs = new Dictionary<string, object?>
        {
            ["count"] = 42L,
            ["label"] = "hello",
            ["flag"] = true,
            ["ratio"] = 3.14,
        };
        var result = ProtoMapping.AttributesToDomain(ProtoMapping.AttributesToProto(attrs));
        result["count"].ShouldBe(42L);
        result["label"].ShouldBe("hello");
        result["flag"].ShouldBe(true);
        ((double)result["ratio"]!).ShouldBeInRange(3.13, 3.15);
    }

    [Fact]
    public void RequestContext_now_and_subject_round_trip()
    {
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var ctx = new RequestContext(now, new SubjectRef("user", "u-1", null), new Dictionary<string, object?>());
        var result = ProtoMapping.ToDomain(ProtoMapping.ToProto(ctx));
        result.Now.ShouldBe(now, TimeSpan.FromMilliseconds(1));
        result.Subject.ShouldBe(ctx.Subject);
    }
}
