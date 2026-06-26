using CsCheck;

using Custodex.Abstractions;
using Custodex.Core.Dsl;
using Custodex.Core.Dsl.Parsing;

using Shouldly;

namespace Custodex.Core.Tests.Dsl;

public class RoundTripTests
{
    private static bool SchemaStructEquals(Schema a, Schema b)
    {
        if (a.Version != b.Version) return false;
        if (a.Types.Count != b.Types.Count) return false;
        if (a.Conditions.Count != b.Conditions.Count) return false;

        for (var i = 0; i < a.Types.Count; i++)
        {
            if (!TypeStructEquals(a.Types[i], b.Types[i])) return false;
        }

        for (var i = 0; i < a.Conditions.Count; i++)
        {
            if (!CondStructEquals(a.Conditions[i], b.Conditions[i])) return false;
        }

        return true;
    }

    private static bool TypeStructEquals(EntityTypeDef a, EntityTypeDef b)
    {
        if (a.Name != b.Name) return false;
        if (a.Relations.Count != b.Relations.Count) return false;
        if (a.Permissions.Count != b.Permissions.Count) return false;

        for (var i = 0; i < a.Relations.Count; i++)
        {
            var ar = a.Relations[i];
            var br = b.Relations[i];
            if (ar.Name != br.Name) return false;
            if (!ar.AllowedSubjects.SequenceEqual(br.AllowedSubjects)) return false;
        }

        for (var i = 0; i < a.Permissions.Count; i++)
        {
            var ap = a.Permissions[i];
            var bp = b.Permissions[i];
            if (ap.Name != bp.Name) return false;
            if (ap.Expression != bp.Expression) return false;
        }

        return true;
    }

    private static bool CondStructEquals(ConditionDef a, ConditionDef b) =>
        a.Name == b.Name &&
        a.Parameters.SequenceEqual(b.Parameters) &&
        a.Body == b.Body;

    [Fact]
    public void Write_then_parse_reproduces_original_schema_for_known_text()
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
        var reparsed = SchemaParser.Parse(SchemaWriter.Write(schema));

        SchemaStructEquals(reparsed, schema).ShouldBeTrue();
    }

    [Fact]
    public void Generated_schemas_round_trip_parse_write_parse_equals_original()
    {
        Check.Sample(SchemaGenerators.SchemaGen, schema =>
        {
            var reparsed = SchemaParser.Parse(SchemaWriter.Write(schema));
            return SchemaStructEquals(reparsed, schema);
        });
    }
}
