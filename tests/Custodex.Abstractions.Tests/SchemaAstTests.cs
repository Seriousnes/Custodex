using Shouldly;

namespace Custodex.Abstractions.Tests;

public class SchemaAstTests
{
    [Fact]
    public void PermExpr_subtypes_are_pattern_matchable()
    {
        PermExpr expr = new Union(new RelationRef("medicator"), new Arrow("enclosure", "edit"));
        var label = expr switch
        {
            Union => "union",
            Arrow => "arrow",
            _ => "other"
        };
        label.ShouldBe("union");
    }
}
