using Custodex.TestKit;
using Shouldly;

namespace Custodex.Abstractions.Tests;

public class SchemaAstTests
{
    [Fact]
    public void PermExpr_subtypes_are_pattern_matchable()
    {
        var world = TestWorld.New();
        PermExpr expr = new Union(new RelationRef(world.Relation()), new Arrow(world.Relation(), world.Permission()));
        var label = expr switch
        {
            Union => "union",
            Arrow => "arrow",
            _ => "other"
        };
        label.ShouldBe("union");
    }
}
