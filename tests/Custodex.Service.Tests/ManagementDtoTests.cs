using Custodex.Service.Rest;

using Shouldly;

namespace Custodex.Service.Tests;

public sealed class ManagementDtoTests
{
    [Fact]
    public void RelationTupleDto_with_condition_and_subject_set_maps_correctly()
    {
        var dto = new RelationTupleDto(
            Object: new EntityRefDto("doc", "d-1"),
            Relation: "viewer",
            Subject: new SubjectRefDto("grp", "g-1", "member"),
            Condition: new ConditionRefDto("working-hours",
                new Dictionary<string, object?> { ["start"] = 9.0, ["end"] = 17.0 }));

        var record = RestMap.FromDto(dto);

        record.Object.Type.ShouldBe("doc");
        record.Object.Id.ShouldBe("d-1");
        record.Relation.ShouldBe("viewer");
        record.Subject.IsSubjectSet.ShouldBeTrue();
        record.Condition.ShouldNotBeNull();
        record.Condition!.Name.ShouldBe("working-hours");
        record.Condition.Parameters.ShouldContainKey("start");
    }

    [Fact]
    public void RelationTupleDto_without_condition_maps_to_unconditioned_tuple()
    {
        var dto = new RelationTupleDto(
            Object: new EntityRefDto("doc", "d-2"),
            Relation: "owner",
            Subject: new SubjectRefDto("user", "u-1", null),
            Condition: null);

        var record = RestMap.FromDto(dto);

        record.Condition.ShouldBeNull();
    }

    [Fact]
    public void RelationTupleDto_round_trips_via_ToDto()
    {
        var dto = new RelationTupleDto(
            Object: new EntityRefDto("doc", "d-3"),
            Relation: "editor",
            Subject: new SubjectRefDto("user", "u-2", null),
            Condition: null);

        var record = RestMap.FromDto(dto);
        var back = RestMap.ToDto(record);

        back.ShouldBe(dto);
    }
}
