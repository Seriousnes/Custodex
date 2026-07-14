using Custodex.Protos;
using Custodex.TestKit;

using Shouldly;

namespace Custodex.Service.Tests;

public sealed class ProtoMapTests
{
    [Fact]
    public void Consistency_at_least_as_fresh_round_trips_through_request_context()
    {
        var world = TestWorld.New();
        var token = Abstractions.ConsistencyToken.Create(world.Tenant, epoch: 5, changeLogId: 9);
        var ctx = new Abstractions.RequestContext(
            DateTimeOffset.UnixEpoch, world.User(world.SubjectId()), new Dictionary<string, object?>(),
            Abstractions.Consistency.AtLeastAsFresh(token));

        var back = ProtoMap.FromProto(ProtoMap.ToProto(ctx));

        back.Consistency!.Mode.ShouldBe(Abstractions.ConsistencyMode.AtLeastAsFresh);
        back.Consistency.Token!.Value.ShouldBe(token.Value);
    }

    [Fact]
    public void Consistency_fully_consistent_round_trips_through_request_context()
    {
        var world = TestWorld.New();
        var ctx = new Abstractions.RequestContext(
            DateTimeOffset.UnixEpoch, world.User(world.SubjectId()), new Dictionary<string, object?>(),
            Abstractions.Consistency.FullyConsistent);

        var back = ProtoMap.FromProto(ProtoMap.ToProto(ctx));

        back.Consistency!.Mode.ShouldBe(Abstractions.ConsistencyMode.FullyConsistent);
    }

    [Fact]
    public void Absent_consistency_maps_to_null()
    {
        var world = TestWorld.New();
        var ctx = new Abstractions.RequestContext(
            DateTimeOffset.UnixEpoch, world.User(world.SubjectId()), new Dictionary<string, object?>());

        var back = ProtoMap.FromProto(ProtoMap.ToProto(ctx));

        back.Consistency.ShouldBeNull();
    }

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
        var proto = new Api.SubjectRef { Type = "user", Id = "alice", Relation = "" };
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
        var node = new Api.ExplainNode
        {
            Description = "root",
            Allowed = true,
        };
        node.Children.Add(new Api.ExplainNode { Description = "child1", Allowed = true });
        node.Children.Add(new Api.ExplainNode { Description = "child2", Allowed = false });

        var back = ProtoMap.FromProto(node);

        back.Description.ShouldBe("root");
        back.Allowed.ShouldBeTrue();
        back.Children.Count.ShouldBe(2);
        back.Children[0].Description.ShouldBe("child1");
        back.Children[1].Allowed.ShouldBeFalse();
    }

    [Fact]
    public void Struct_integral_number_decodes_to_long_not_double()
    {
        var s = ProtoMap.ToStruct(new Dictionary<string, object?> { ["n"] = 42L });
        var back = ProtoMap.FromStruct(s);

        back["n"].ShouldBeOfType<long>();
        back["n"].ShouldBe(42L);
    }

    [Fact]
    public void ConditionRef_integer_parameter_decodes_to_long()
    {
        var record = new Abstractions.ConditionRef("c",
            new Dictionary<string, object?> { ["x"] = 5L });

        var back = ProtoMap.FromProto(ProtoMap.ToProto(record));

        back.Parameters["x"].ShouldBeOfType<long>();
    }

    [Fact]
    public void FromStruct_null_decodes_to_empty_dictionary()
    {
        ProtoMap.FromStruct(null).ShouldBeEmpty();
    }

    [Fact]
    public void MetricsSnapshot_round_trips_all_fields()
    {
        var captured = new DateTimeOffset(2026, 7, 14, 9, 15, 0, TimeSpan.Zero);
        var record = new Abstractions.MetricsSnapshot(captured, 7, 1.5, 2.5, 3.5, 11, 22, 33);

        var back = ProtoMap.FromProto(ProtoMap.ToProto(record));

        back.CapturedAt.ShouldBe(captured);
        back.CheckCount.ShouldBe(7);
        back.CheckP50Ms.ShouldBe(1.5);
        back.CheckP95Ms.ShouldBe(2.5);
        back.CheckP99Ms.ShouldBe(3.5);
        back.CacheHits.ShouldBe(11);
        back.CacheMisses.ShouldBe(22);
        back.CacheSwept.ShouldBe(33);
    }

    [Fact]
    public void MetricsSnapshot_missing_captured_at_decodes_to_epoch()
    {
        var proto = new Api.MetricsSnapshot { CheckCount = 0 };

        ProtoMap.FromProto(proto).CapturedAt.ShouldBe(DateTimeOffset.UnixEpoch);
    }

    [Fact]
    public void DateTimeOffset_attribute_serializes_as_invariant_iso8601()
    {
        var when = new DateTimeOffset(2026, 6, 25, 8, 30, 0, TimeSpan.Zero);

        var s = ProtoMap.ToStruct(new Dictionary<string, object?> { ["t"] = when });

        s.Fields["t"].StringValue.ShouldBe(when.ToString("O"));
        ProtoMap.FromStruct(s)["t"].ShouldBe(when.ToString("O"));
    }
}
