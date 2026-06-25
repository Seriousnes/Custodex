using Custodex.Client.Serialization;
using Custodex.Abstractions;
using Custodex.Core;
using Shouldly;

namespace Custodex.Client.Tests;

public sealed class SchemaJsonTests
{
    [Fact]
    public void Schema_with_complex_algebra_round_trips()
    {
        var schema = new SchemaBuilder("v1")
            .Type("folder", t => t
                .Relation("owner", s => s.Type("user")))
            .Type("doc", t => t
                .Relation("parent", s => s.Type("folder"))
                .Relation("editor", s => s.Type("user"))
                .Permission("view", p => p
                    .Relation("editor")
                    .Arrow("parent", "view"))
                .Permission("delete", p => p
                    .Relation("editor")
                    .Exclude(e => e.Arrow("parent", "view"))))
            .Build();

        var json = SchemaJson.Serialize(schema);
        var result = SchemaJson.Deserialize(json);

        result.ShouldNotBeNull();
        result!.Version.ShouldBe("v1");
        result.Types.Count.ShouldBe(schema.Types.Count);
    }
}
