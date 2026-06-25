using Custodex.Abstractions;
using Custodex.V1;
using Google.Protobuf.WellKnownTypes;

namespace Custodex.Protos;

/// <summary>
/// Single source of truth for converting between the canonical <see cref="Custodex.Abstractions"/>
/// records and the generated <c>Custodex.V1</c> protobuf messages. Every shared type has a
/// <c>ToProto</c> (domain → proto) and a <c>FromProto</c> (proto → domain) form so both the gRPC
/// service and client map the wire contract identically.
/// </summary>
public static class ProtoMap
{
    /// <summary>Converts a domain <see cref="Abstractions.EntityRef"/> to its proto form.</summary>
    public static V1.EntityRef ToProto(Abstractions.EntityRef r) => new() { Type = r.Type, Id = r.Id };

    /// <summary>Converts a proto <see cref="V1.EntityRef"/> to its domain form.</summary>
    public static Abstractions.EntityRef FromProto(V1.EntityRef p) => new(p.Type, p.Id);

    /// <summary>
    /// Converts a domain <see cref="Abstractions.SubjectRef"/> to its proto form. An empty
    /// <c>relation</c> field encodes a plain subject (not a subject-set).
    /// </summary>
    public static V1.SubjectRef ToProto(Abstractions.SubjectRef r) =>
        new() { Type = r.Type, Id = r.Id, Relation = r.Relation ?? string.Empty };

    /// <summary>
    /// Converts a proto <see cref="V1.SubjectRef"/> to its domain form. An empty <c>relation</c>
    /// field maps to <see langword="null"/> so <see cref="Abstractions.SubjectRef.IsSubjectSet"/> stays correct.
    /// </summary>
    public static Abstractions.SubjectRef FromProto(V1.SubjectRef p) =>
        new(p.Type, p.Id, string.IsNullOrEmpty(p.Relation) ? null : p.Relation);

    /// <summary>Converts a domain <see cref="Abstractions.TenantContext"/> to its proto form.</summary>
    public static V1.TenantContext ToProto(Abstractions.TenantContext t) =>
        new() { Store = t.Store, Tenant = t.Tenant };

    /// <summary>Converts a proto <see cref="V1.TenantContext"/> to its domain form.</summary>
    public static Abstractions.TenantContext FromProto(V1.TenantContext p) => new(p.Store, p.Tenant);

    /// <summary>Converts a domain <see cref="Abstractions.ConditionRef"/> to its proto form.</summary>
    public static V1.ConditionRef ToProto(Abstractions.ConditionRef c) =>
        new() { Name = c.Name, Parameters = ToStruct(c.Parameters) };

    /// <summary>Converts a proto <see cref="V1.ConditionRef"/> to its domain form.</summary>
    public static Abstractions.ConditionRef FromProto(V1.ConditionRef p) =>
        new(p.Name, FromStruct(p.Parameters));

    /// <summary>Converts a domain <see cref="Abstractions.RelationTuple"/> to its proto form.</summary>
    public static V1.RelationTuple ToProto(Abstractions.RelationTuple t)
    {
        var proto = new V1.RelationTuple
        {
            Object = ToProto(t.Object),
            Relation = t.Relation,
            Subject = ToProto(t.Subject),
        };
        if (t.Condition is not null)
            proto.Condition = ToProto(t.Condition);
        return proto;
    }

    /// <summary>Converts a proto <see cref="V1.RelationTuple"/> to its domain form.</summary>
    public static Abstractions.RelationTuple FromProto(V1.RelationTuple p) =>
        new(FromProto(p.Object), p.Relation, FromProto(p.Subject),
            p.Condition is { Name.Length: > 0 } ? FromProto(p.Condition) : null);

    /// <summary>Converts a domain <see cref="Abstractions.RequestContext"/> to its proto form.</summary>
    public static V1.RequestContext ToProto(Abstractions.RequestContext ctx) => new()
    {
        Now = Timestamp.FromDateTimeOffset(ctx.Now),
        Subject = ToProto(ctx.Subject),
        Attributes = ToStruct(ctx.Attributes),
    };

    /// <summary>
    /// Converts a proto <see cref="V1.RequestContext"/> to its domain form. A missing <c>now</c> or
    /// <c>subject</c> decodes to a default rather than throwing.
    /// </summary>
    public static Abstractions.RequestContext FromProto(V1.RequestContext p) => new(
        p.Now is null ? DateTimeOffset.UtcNow : p.Now.ToDateTimeOffset(),
        p.Subject is null ? new Abstractions.SubjectRef("*", "*") : FromProto(p.Subject),
        FromStruct(p.Attributes));

    /// <summary>Converts a domain <see cref="Abstractions.ExplainNode"/> to its proto form.</summary>
    public static V1.ExplainNode ToProto(Abstractions.ExplainNode n)
    {
        var proto = new V1.ExplainNode { Description = n.Description, Allowed = n.Allowed };
        foreach (var child in n.Children)
            proto.Children.Add(ToProto(child));
        return proto;
    }

    /// <summary>Converts a proto <see cref="V1.ExplainNode"/> to its domain form.</summary>
    public static Abstractions.ExplainNode FromProto(V1.ExplainNode p) =>
        new(p.Description, p.Allowed, [.. p.Children.Select(FromProto)]);

    /// <summary>
    /// Converts a domain attribute dictionary to a <see cref="Struct"/>. Integers (<see cref="int"/>,
    /// <see cref="long"/>), floats, bools and strings map to their JSON value kinds;
    /// <see cref="DateTimeOffset"/>/<see cref="DateTime"/> serialize as culture-invariant ISO-8601.
    /// </summary>
    public static Struct ToStruct(IReadOnlyDictionary<string, object?> dict)
    {
        var s = new Struct();
        foreach (var (k, v) in dict)
            s.Fields[k] = ToValue(v);
        return s;
    }

    /// <summary>
    /// Converts a <see cref="Struct"/> to a domain attribute dictionary keyed ordinally. An integral
    /// number decodes to <see cref="long"/> (so the condition evaluator's integer parameters match);
    /// a <see langword="null"/> struct — which an omitted proto field decodes to — maps to an empty dictionary.
    /// </summary>
    public static IReadOnlyDictionary<string, object?> FromStruct(Struct? s) =>
        s is null
            ? new Dictionary<string, object?>(StringComparer.Ordinal)
            : s.Fields.ToDictionary(kv => kv.Key, kv => FromValue(kv.Value), StringComparer.Ordinal);

    private static Value ToValue(object? v) => v switch
    {
        null => Value.ForNull(),
        bool b => Value.ForBool(b),
        int i => Value.ForNumber(i),
        long l => Value.ForNumber(l),
        float f => Value.ForNumber(f),
        double d => Value.ForNumber(d),
        string str => Value.ForString(str),
        DateTimeOffset dto => Value.ForString(dto.ToString("O")),
        DateTime dt => Value.ForString(dt.ToString("O")),
        _ => Value.ForString(v.ToString() ?? string.Empty),
    };

    private static object? FromValue(Value v) => v.KindCase switch
    {
        Value.KindOneofCase.NullValue => null,
        Value.KindOneofCase.BoolValue => v.BoolValue,
        Value.KindOneofCase.StringValue => v.StringValue,
        Value.KindOneofCase.NumberValue => IsIntegral(v.NumberValue) ? (object)(long)v.NumberValue : v.NumberValue,
        _ => null,
    };

    private static bool IsIntegral(double d) => d == Math.Floor(d) && d >= long.MinValue && d <= long.MaxValue;
}
