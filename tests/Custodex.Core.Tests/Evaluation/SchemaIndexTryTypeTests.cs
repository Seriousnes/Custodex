using Custodex.Core.Evaluation;

using Shouldly;

namespace Custodex.Core.Tests.Evaluation;

public class SchemaIndexTryTypeTests
{
    private static SchemaIndex BuildIndex() =>
        new(new SchemaBuilder("v1")
            .Type("doc", t => t
                .Relation("viewer", s => s.User())
                .Permission("view", p => p.Relation("viewer")))
            .Build());

    [Fact]
    public void TryType_returns_true_and_def_for_known_type()
    {
        var idx = BuildIndex();
        idx.TryType("doc", out var def).ShouldBeTrue();
        def.Name.ShouldBe("doc");
    }

    [Fact]
    public void TryType_returns_false_for_unknown_type()
    {
        var idx = BuildIndex();
        idx.TryType("nope", out _).ShouldBeFalse();
    }
}
