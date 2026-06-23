using Relkit.Abstractions;
using Shouldly;
using Xunit;

namespace Relkit.Core.Tests;

public class FillerAndParamBuilderTests
{
    [Fact]
    public void Filler_builder_collects_user_subjectset_and_wildcard()
    {
        var fillers = new SubjectFillerBuilder().User().SubjectSet("group", "member").Wildcard("user").Build();
        fillers.ShouldContain(new SubjectTypeRef("user", null, false));
        fillers.ShouldContain(new SubjectTypeRef("group", "member", false));
        fillers.ShouldContain(new SubjectTypeRef("user", null, true));
    }

    [Fact]
    public void Param_builder_collects_typed_params()
    {
        var ps = new ConditionParamBuilder().Int("start").Int("end").Build();
        ps.ShouldBe(new[] { new ConditionParam("start", ConditionType.Int), new ConditionParam("end", ConditionType.Int) });
    }
}
