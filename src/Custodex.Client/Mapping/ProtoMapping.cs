using Custodex.Abstractions;
using Google.Protobuf.WellKnownTypes;
using ProtoV1 = Custodex.V1;

namespace Custodex.Client.Mapping;

/// <summary>
/// Converts between canonical <see cref="Custodex.Abstractions"/> records and
/// the generated <c>Custodex.V1</c> proto messages.
/// </summary>
public static class ProtoMapping
{
    /// <summary>Converts a domain <see cref="EntityRef"/> to its proto representation.</summary>
    public static ProtoV1.EntityRef ToProto(EntityRef r) =>
        new() { Type = r.Type, Id = r.Id };

    /// <summary>Converts a proto <see cref="ProtoV1.EntityRef"/> to its domain representation.</summary>
    public static EntityRef ToDomain(ProtoV1.EntityRef p) => new(p.Type, p.Id);

    /// <summary>Converts a domain <see cref="SubjectRef"/> to its proto representation.</summary>
    public static ProtoV1.SubjectRef ToProto(SubjectRef r) =>
        new() { Type = r.Type, Id = r.Id, Relation = r.Relation ?? string.Empty };

    /// <summary>Converts a proto <see cref="ProtoV1.SubjectRef"/> to its domain representation.</summary>
    public static SubjectRef ToDomain(ProtoV1.SubjectRef p) =>
        new(p.Type, p.Id, string.IsNullOrEmpty(p.Relation) ? null : p.Relation);

    /// <summary>Converts a domain <see cref="TenantContext"/> to its proto representation.</summary>
    public static ProtoV1.TenantContext ToProto(TenantContext t) =>
        new() { Store = t.Store, Tenant = t.Tenant };

    /// <summary>Converts a proto <see cref="ProtoV1.TenantContext"/> to its domain representation.</summary>
    public static TenantContext ToDomain(ProtoV1.TenantContext p) => new(p.Store, p.Tenant);

    /// <summary>Converts a domain <see cref="ConditionRef"/> to its proto representation.</summary>
    public static ProtoV1.ConditionRef ToProto(ConditionRef c) =>
        new() { Name = c.Name, Parameters = AttributesToProto(c.Parameters) };

    /// <summary>Converts a proto <see cref="ProtoV1.ConditionRef"/> to its domain representation.</summary>
    public static ConditionRef ToDomain(ProtoV1.ConditionRef p) =>
        new(p.Name, AttributesToDomain(p.Parameters));

    /// <summary>Converts a domain <see cref="RelationTuple"/> to its proto representation.</summary>
    public static ProtoV1.RelationTuple ToProto(RelationTuple t)
    {
        var proto = new ProtoV1.RelationTuple
        {
            Object = ToProto(t.Object),
            Relation = t.Relation,
            Subject = ToProto(t.Subject),
        };
        if (t.Condition is not null)
            proto.Condition = ToProto(t.Condition);
        return proto;
    }

    /// <summary>Converts a proto <see cref="ProtoV1.RelationTuple"/> to its domain representation.</summary>
    public static RelationTuple ToDomain(ProtoV1.RelationTuple p) =>
        new(ToDomain(p.Object), p.Relation, ToDomain(p.Subject),
            p.Condition is { Name.Length: > 0 } ? ToDomain(p.Condition) : null);

    /// <summary>Converts a domain <see cref="RequestContext"/> to its proto representation.</summary>
    public static ProtoV1.RequestContext ToProto(RequestContext ctx) =>
        new()
        {
            Now = Timestamp.FromDateTimeOffset(ctx.Now),
            Subject = ToProto(ctx.Subject),
            Attributes = AttributesToProto(ctx.Attributes),
        };

    /// <summary>Converts a proto <see cref="ProtoV1.RequestContext"/> to its domain representation.</summary>
    public static RequestContext ToDomain(ProtoV1.RequestContext p) =>
        new(p.Now?.ToDateTimeOffset() ?? DateTimeOffset.UtcNow,
            ToDomain(p.Subject),
            p.Attributes is not null ? AttributesToDomain(p.Attributes) : new Dictionary<string, object?>(StringComparer.Ordinal));

    /// <summary>Converts a domain <see cref="ExplainNode"/> to its proto representation.</summary>
    public static ProtoV1.ExplainNode ToProto(ExplainNode node)
    {
        var proto = new ProtoV1.ExplainNode
        {
            Description = node.Description,
            Allowed = node.Allowed,
        };
        foreach (var child in node.Children)
            proto.Children.Add(ToProto(child));
        return proto;
    }

    /// <summary>Converts a proto <see cref="ProtoV1.ExplainNode"/> to its domain representation.</summary>
    public static ExplainNode ToDomain(ProtoV1.ExplainNode p) =>
        new(p.Description, p.Allowed, p.Children.Select(ToDomain).ToList());

    /// <summary>Converts domain attribute dictionary to a <see cref="Struct"/>.</summary>
    public static Struct AttributesToProto(IReadOnlyDictionary<string, object?> attrs)
    {
        var s = new Struct();
        foreach (var (key, val) in attrs)
            s.Fields[key] = ObjectToValue(val);
        return s;
    }

    /// <summary>Converts a <see cref="Struct"/> to a domain attribute dictionary.</summary>
    public static IReadOnlyDictionary<string, object?> AttributesToDomain(Struct s) =>
        s.Fields.ToDictionary(
            kv => kv.Key,
            kv => ValueToObject(kv.Value),
            StringComparer.Ordinal);

    private static Value ObjectToValue(object? obj) => obj switch
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
        _ => Value.ForString(obj.ToString() ?? string.Empty),
    };

    private static object? ValueToObject(Value v) => v.KindCase switch
    {
        Value.KindOneofCase.NullValue => null,
        Value.KindOneofCase.BoolValue => v.BoolValue,
        Value.KindOneofCase.StringValue => v.StringValue,
        Value.KindOneofCase.NumberValue => IsIntegral(v.NumberValue)
            ? (object)(long)v.NumberValue
            : v.NumberValue,
        _ => null,
    };

    private static bool IsIntegral(double d) => d == Math.Floor(d) && d >= long.MinValue && d <= long.MaxValue;
}
