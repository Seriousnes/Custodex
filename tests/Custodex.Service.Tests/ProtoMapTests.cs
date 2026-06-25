using Custodex.Abstractions;
using Custodex.V1;
using Custodex.Service.Mapping;
using Google.Protobuf.WellKnownTypes;
using Shouldly;

namespace Custodex.Service.Tests;

public sealed class ProtoMapTests
{
    [Fact]
    public void EntityRef_wildcard_round_trips()
    {
        var record = new Abstractions.EntityRef("user", "*");
        var proto = ProtoMap.ToProto(record);
        var back = ProtoMap.FromProto(proto);

        back.ShouldBe(record);
        back.IsWildcard.ShouldBeTrue();
    }

    [Fact]
    public void SubjectRef_with_empty_relation_maps_to_null_Relation()
    {
        var proto = new V1.SubjectRef { Type = "user", Id = "alice", Relation = "" };
        var back = ProtoMap.FromProto(proto);

        back.Relation.ShouldBeNull();
        back.IsSubjectSet.ShouldBeFalse();
    }

    [Fact]
    public void SubjectRef_with_relation_round_trips()
    {
        var record = new Abstractions.SubjectRef("group", "grp-7", "member");
        var proto = ProtoMap.ToProto(record);
        var back = ProtoMap.FromProto(proto);

        back.ShouldBe(record);
        back.Relation.ShouldBe("member");
    }

    [Fact]
    public void Struct_round_trips_mixed_value_types()
    {
        var dict = new Dictionary<string, object?>
        {
            ["n"] = 42L,
            ["d"] = 1.5,
            ["b"] = true,
            ["s"] = "hello",
            ["x"] = (object?)null,
        };

        var s = ProtoMap.ToStruct(dict);
        var back = ProtoMap.FromStruct(s);

        Convert.ToInt32(back["n"]).ShouldBe(42);
        Convert.ToDouble(back["d"]).ShouldBe(1.5);
        back["b"].ShouldBe(true);
        back["s"].ShouldBe("hello");
        back["x"].ShouldBeNull();
    }

    [Fact]
    public void ConditionRef_round_trips_with_int_parameters()
    {
        var record = new Abstractions.ConditionRef("within_window",
            new Dictionary<string, object?> { ["start"] = 8L, ["end"] = 18L });

        var proto = ProtoMap.ToProto(record);
        var back = ProtoMap.FromProto(proto);

        back.Name.ShouldBe("within_window");
        Convert.ToInt32(back.Parameters["start"]).ShouldBe(8);
        Convert.ToInt32(back.Parameters["end"]).ShouldBe(18);
    }

    [Fact]
    public void RequestContext_maps_now_subject_and_attributes()
    {
        var now = new DateTimeOffset(2026, 6, 25, 10, 0, 0, TimeSpan.Zero);
        var subject = new Abstractions.SubjectRef("user", "alice");
        var attrs = new Dictionary<string, object?> { ["role"] = "admin" };
        var record = new Abstractions.RequestContext(now, subject, attrs);

        var proto = ProtoMap.ToProto(record);
        var back = ProtoMap.FromProto(proto);

        back.Now.ShouldBe(now);
        back.Subject.ShouldBe(subject);
        back.Attributes["role"].ShouldBe("admin");
    }

    [Fact]
    public void ExplainNode_with_two_children_maps_recursively()
    {
        var node = new V1.ExplainNode
        {
            Description = "root",
            Allowed = true,
        };
        node.Children.Add(new V1.ExplainNode { Description = "child1", Allowed = true });
        node.Children.Add(new V1.ExplainNode { Description = "child2", Allowed = false });

        var back = ProtoMap.FromProto(node);

        back.Description.ShouldBe("root");
        back.Allowed.ShouldBeTrue();
        back.Children.Count.ShouldBe(2);
        back.Children[0].Description.ShouldBe("child1");
        back.Children[1].Allowed.ShouldBeFalse();
    }
}
