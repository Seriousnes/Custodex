using CsCheck;
using Custodex.Core.Dsl;
using Custodex.Core.Dsl.Parsing;
using Shouldly;

namespace Custodex.Core.Tests.Dsl;

public class RoundTripTests
{
    [Fact]
    public void Write_is_stable_on_reparse_for_known_text()
    {
        const string src = """
            type group {
                relation member: user | group#member
            }
            type widget {
                relation owner: user
                relation viewer: user | group#member | user:*
                relation blocked: user
                permission view = owner + viewer - blocked
            }
            condition gated(start: int, end: int) = (hour(context.now) >= start) && (hour(context.now) < end)
            """;

        var schema = SchemaParser.Parse(src);
        var text1 = SchemaWriter.Write(schema);
        var text2 = SchemaWriter.Write(SchemaParser.Parse(text1));

        text1.ShouldBe(text2);
    }

    [Fact]
    public void Generated_schemas_round_trip_via_write_stability()
    {
        Check.Sample(SchemaGenerators.SchemaGen, schema =>
        {
            var text1 = SchemaWriter.Write(schema);
            var text2 = SchemaWriter.Write(SchemaParser.Parse(text1));
            return text1 == text2;
        });
    }
}
