using Custodex.Abstractions;
using Custodex.TestKit;
using Shouldly;

namespace Custodex.Core.Tests;

public class FillerAndParamBuilderTests
{
    [Fact]
    public void Filler_builder_collects_type_subjectset_and_wildcard()
    {
        var world = TestWorld.New();
        var fillers = new SubjectFillerBuilder()
            .Type(world.UserType)
            .SubjectSet(world.GroupType, world.MemberRelation)
            .Wildcard(world.UserType)
            .Build();
        fillers.ShouldContain(new SubjectTypeRef(world.UserType, null, false));
        fillers.ShouldContain(new SubjectTypeRef(world.GroupType, world.MemberRelation, false));
        fillers.ShouldContain(new SubjectTypeRef(world.UserType, null, true));
    }

    [Fact]
    public void Param_builder_collects_typed_params()
    {
        var world = TestWorld.New();
        var start = world.ParamName();
        var end = world.ParamName();
        var ps = new ConditionParamBuilder().Int(start).Int(end).Build();
        ps.ShouldBe(new[] { new ConditionParam(start, ConditionType.Int), new ConditionParam(end, ConditionType.Int) });
    }
}
