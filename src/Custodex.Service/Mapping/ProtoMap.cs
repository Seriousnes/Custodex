using Custodex.Abstractions;
using Custodex.V1;
using Google.Protobuf.WellKnownTypes;

namespace Custodex.Service.Mapping;

/// <summary>
/// Converts between <see cref="Custodex.Abstractions"/> records and the generated protobuf message types.
/// Every shared type has a <c>ToProto</c> and a <c>FromProto</c> overload here so the mapping is
/// defined and tested once.
/// </summary>
public static class ProtoMap
{
    /// <summary>Converts an <see cref="Abstractions.EntityRef"/> to its proto representation.</summary>
    public static V1.EntityRef ToProto(Abstractions.EntityRef r) => new() { Type = r.Type, Id = r.Id };

    /// <summary>Converts a proto <see cref="V1.EntityRef"/> to an <see cref="Abstractions.EntityRef"/>.</summary>
    public static Abstractions.EntityRef FromProto(V1.EntityRef p) => new(p.Type, p.Id);

    /// <summary>
    /// Converts an <see cref="Abstractions.SubjectRef"/> to its proto representation.
    /// An empty <c>relation</c> field encodes a plain subject (not a subject-set).
    /// </summary>
    public static V1.SubjectRef ToProto(Abstractions.SubjectRef r) =>
        new() { Type = r.Type, Id = r.Id, Relation = r.Relation ?? string.Empty };

    /// <summary>
    /// Converts a proto <see cref="V1.SubjectRef"/> to an <see cref="Abstractions.SubjectRef"/>.
    /// An empty <c>relation</c> field maps to <see langword="null"/> so <see cref="Abstractions.SubjectRef.IsSubjectSet"/> stays correct.
    /// </summary>
    public static Abstractions.SubjectRef FromProto(V1.SubjectRef p) =>
        new(p.Type, p.Id, string.IsNullOrEmpty(p.Relation) ? null : p.Relation);

    /// <summary>Converts an <see cref="Abstractions.ConditionRef"/> to its proto representation.</summary>
    public static V1.ConditionRef ToProto(Abstractions.ConditionRef r) =>
        new() { Name = r.Name, Parameters = ToStruct(r.Parameters) };

    /// <summary>Converts a proto <see cref="V1.ConditionRef"/> to an <see cref="Abstractions.ConditionRef"/>.</summary>
    public static Abstractions.ConditionRef FromProto(V1.ConditionRef p) =>
        new(p.Name, FromStruct(p.Parameters));

    /// <summary>Converts an <see cref="Abstractions.RequestContext"/> to its proto representation.</summary>
    public static V1.RequestContext ToProto(Abstractions.RequestContext ctx) => new()
    {
        Now = Timestamp.FromDateTimeOffset(ctx.Now),
        Subject = ToProto(ctx.Subject),
        Attributes = ToStruct(ctx.Attributes),
    };

    /// <summary>
    /// Converts a proto <see cref="V1.RequestContext"/> to an <see cref="Abstractions.RequestContext"/>.
    /// A missing <c>now</c> or <c>subject</c> decodes to defaults rather than throwing.
    /// </summary>
    public static Abstractions.RequestContext FromProto(V1.RequestContext p) => new(
        p.Now is null ? DateTimeOffset.UtcNow : p.Now.ToDateTimeOffset(),
        p.Subject is null ? new Abstractions.SubjectRef("*", "*") : FromProto(p.Subject),
        p.Attributes is null ? new Dictionary<string, object?>() : FromStruct(p.Attributes));

    /// <summary>Converts a <see cref="TenantContext"/> message to an <see cref="Abstractions.TenantContext"/>.</summary>
    public static Abstractions.TenantContext FromProto(V1.TenantContext p) =>
        new(p.Store, p.Tenant);

    /// <summary>Converts a proto <see cref="V1.ExplainNode"/> to an <see cref="Abstractions.ExplainNode"/>.</summary>
    public static Abstractions.ExplainNode FromProto(V1.ExplainNode p) =>
        new(p.Description, p.Allowed, [.. p.Children.Select(c => FromProto(c))]);

    /// <summary>
    /// Converts an <see cref="Abstractions.ExplainNode"/> to its proto representation.
    /// </summary>
    public static V1.ExplainNode ToProto(Abstractions.ExplainNode n)
    {
        var proto = new V1.ExplainNode { Description = n.Description, Allowed = n.Allowed };
        foreach (var child in n.Children)
            proto.Children.Add(ToProto(child));
        return proto;
    }

    /// <summary>
    /// Converts an <see cref="IReadOnlyDictionary{String, Object}"/> to a <see cref="Struct"/>.
    /// Numeric values (int, long, float, double) map to <see cref="Value.KindOneofCase.NumberValue"/>;
    /// bool to <see cref="Value.KindOneofCase.BoolValue"/>; string to
    /// <see cref="Value.KindOneofCase.StringValue"/>; <see langword="null"/> to
    /// <see cref="Value.KindOneofCase.NullValue"/>.
    /// </summary>
    public static Struct ToStruct(IReadOnlyDictionary<string, object?> dict)
    {
        var s = new Struct();
        foreach (var (k, v) in dict)
            s.Fields[k] = ToValue(v);
        return s;
    }

    /// <summary>
    /// Converts a <see cref="Struct"/> to an <see cref="IReadOnlyDictionary{String, Object}"/>.
    /// Numeric values arrive as <c>double</c> after the proto round-trip.
    /// </summary>
    public static IReadOnlyDictionary<string, object?> FromStruct(Struct s) =>
        s.Fields.ToDictionary(kv => kv.Key, kv => FromValue(kv.Value));

    private static Value ToValue(object? v) => v switch
    {
        null => Value.ForNull(),
        bool b => Value.ForBool(b),
        int i => Value.ForNumber(i),
        long l => Value.ForNumber(l),
        float f => Value.ForNumber(f),
        double d => Value.ForNumber(d),
        string str => Value.ForString(str),
        _ => Value.ForString(v.ToString() ?? string.Empty),
    };

    private static object? FromValue(Value v) => v.KindCase switch
    {
        Value.KindOneofCase.NullValue => null,
        Value.KindOneofCase.BoolValue => v.BoolValue,
        Value.KindOneofCase.NumberValue => v.NumberValue,
        Value.KindOneofCase.StringValue => v.StringValue,
        _ => null,
    };
}
