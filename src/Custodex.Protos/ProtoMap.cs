using Google.Protobuf.WellKnownTypes;

namespace Custodex.Protos;

/// <summary>
/// Single source of truth for converting between the canonical <see cref="Custodex.Abstractions"/>
/// records and the generated <c>Custodex.Api</c> protobuf messages. Every shared type has a
/// <c>ToProto</c> (domain → proto) and a <c>FromProto</c> (proto → domain) form so both the gRPC
/// service and client map the wire contract identically.
/// </summary>
public static class ProtoMap
{
    /// <summary>Converts a domain <see cref="Abstractions.EntityRef"/> to its proto form.</summary>
    public static Api.EntityRef ToProto(Abstractions.EntityRef r) => new() { Type = r.Type, Id = r.Id };

    /// <summary>Converts a proto <see cref="Api.EntityRef"/> to its domain form.</summary>
    public static Abstractions.EntityRef FromProto(Api.EntityRef p) => new(p.Type, p.Id);

    /// <summary>
    /// Converts a domain <see cref="Abstractions.SubjectRef"/> to its proto form. An empty
    /// <c>relation</c> field encodes a plain subject (not a subject-set).
    /// </summary>
    public static Api.SubjectRef ToProto(Abstractions.SubjectRef r) =>
        new() { Type = r.Type, Id = r.Id, Relation = r.Relation ?? string.Empty };

    /// <summary>
    /// Converts a proto <see cref="Api.SubjectRef"/> to its domain form. An empty <c>relation</c>
    /// field maps to <see langword="null"/> so <see cref="Abstractions.SubjectRef.IsSubjectSet"/> stays correct.
    /// </summary>
    public static Abstractions.SubjectRef FromProto(Api.SubjectRef p) =>
        new(p.Type, p.Id, string.IsNullOrEmpty(p.Relation) ? null : p.Relation);

    /// <summary>Converts a domain <see cref="Abstractions.TenantContext"/> to its proto form.</summary>
    public static Api.TenantContext ToProto(Abstractions.TenantContext t) =>
        new() { Store = t.Store, Tenant = t.Tenant };

    /// <summary>Converts a proto <see cref="Api.TenantContext"/> to its domain form.</summary>
    public static Abstractions.TenantContext FromProto(Api.TenantContext p) => new(p.Store, p.Tenant);

    /// <summary>Converts a domain <see cref="Abstractions.ConditionRef"/> to its proto form.</summary>
    public static Api.ConditionRef ToProto(Abstractions.ConditionRef c) =>
        new() { Name = c.Name, Parameters = ToStruct(c.Parameters) };

    /// <summary>Converts a proto <see cref="Api.ConditionRef"/> to its domain form.</summary>
    public static Abstractions.ConditionRef FromProto(Api.ConditionRef p) =>
        new(p.Name, FromStruct(p.Parameters));

    /// <summary>Converts a domain <see cref="Abstractions.RelationTuple"/> to its proto form.</summary>
    public static Api.RelationTuple ToProto(Abstractions.RelationTuple t)
    {
        var proto = new Api.RelationTuple
        {
            Object = ToProto(t.Object),
            Relation = t.Relation,
            Subject = ToProto(t.Subject),
        };
        if (t.Condition is not null)
            proto.Condition = ToProto(t.Condition);
        return proto;
    }

    /// <summary>Converts a proto <see cref="Api.RelationTuple"/> to its domain form.</summary>
    public static Abstractions.RelationTuple FromProto(Api.RelationTuple p) =>
        new(FromProto(p.Object), p.Relation, FromProto(p.Subject),
            p.Condition is { Name.Length: > 0 } ? FromProto(p.Condition) : null);

    /// <summary>Converts a domain <see cref="Abstractions.RequestContext"/> to its proto form.</summary>
    public static Api.RequestContext ToProto(Abstractions.RequestContext ctx)
    {
        var proto = new Api.RequestContext
        {
            Now = Timestamp.FromDateTimeOffset(ctx.Now),
            Subject = ToProto(ctx.Subject),
            Attributes = ToStruct(ctx.Attributes),
        };
        if (ctx.Consistency is not null)
            proto.Consistency = ToProto(ctx.Consistency);
        return proto;
    }

    /// <summary>
    /// Converts a proto <see cref="Api.RequestContext"/> to its domain form. A missing <c>now</c> or
    /// <c>subject</c> decodes to a default rather than throwing, and an absent <c>consistency</c> decodes
    /// to <see langword="null"/>.
    /// </summary>
    public static Abstractions.RequestContext FromProto(Api.RequestContext p) => new(
        p.Now is null ? DateTimeOffset.UtcNow : p.Now.ToDateTimeOffset(),
        p.Subject is null ? new Abstractions.SubjectRef("*", "*") : FromProto(p.Subject),
        FromStruct(p.Attributes),
        FromProto(p.Consistency));

    /// <summary>Converts a domain <see cref="Abstractions.Consistency"/> selector to its proto form.</summary>
    public static Api.Consistency ToProto(Abstractions.Consistency c) => new()
    {
        Mode = ToProto(c.Mode),
        Token = c.Token?.Value ?? string.Empty,
    };

    /// <summary>Converts a proto <see cref="Api.Consistency"/> selector to its domain form, or <see langword="null"/> when absent.</summary>
    public static Abstractions.Consistency? FromProto(Api.Consistency? p) => p is null
        ? null
        : p.Mode switch
        {
            Api.ConsistencyMode.AtLeastAsFresh => Abstractions.Consistency.AtLeastAsFresh(new Abstractions.ConsistencyToken(p.Token)),
            Api.ConsistencyMode.FullyConsistent => Abstractions.Consistency.FullyConsistent,
            _ => Abstractions.Consistency.MinimizeLatency,
        };

    /// <summary>Converts a domain <see cref="Abstractions.ConsistencyMode"/> to its proto form.</summary>
    public static Api.ConsistencyMode ToProto(Abstractions.ConsistencyMode m) => m switch
    {
        Abstractions.ConsistencyMode.AtLeastAsFresh => Api.ConsistencyMode.AtLeastAsFresh,
        Abstractions.ConsistencyMode.FullyConsistent => Api.ConsistencyMode.FullyConsistent,
        _ => Api.ConsistencyMode.MinimizeLatency,
    };

    /// <summary>Converts a domain <see cref="Abstractions.ExplainNode"/> to its proto form.</summary>
    public static Api.ExplainNode ToProto(Abstractions.ExplainNode n)
    {
        var proto = new Api.ExplainNode { Description = n.Description, Allowed = n.Allowed };
        foreach (var child in n.Children)
            proto.Children.Add(ToProto(child));
        return proto;
    }

    /// <summary>Converts a proto <see cref="Api.ExplainNode"/> to its domain form.</summary>
    public static Abstractions.ExplainNode FromProto(Api.ExplainNode p) =>
        new(p.Description, p.Allowed, [.. p.Children.Select(FromProto)]);

    /// <summary>Converts a domain <see cref="Abstractions.CheckDecision"/> to its proto form.</summary>
    public static Api.CheckDecision ToProto(Abstractions.CheckDecision d) => d switch
    {
        Abstractions.CheckDecision.Allow => Api.CheckDecision.Allow,
        Abstractions.CheckDecision.Conditional => Api.CheckDecision.Conditional,
        _ => Api.CheckDecision.Deny,
    };

    /// <summary>Converts a proto <see cref="Api.CheckDecision"/> to its domain form. An unrecognised value decodes to deny (fail closed).</summary>
    public static Abstractions.CheckDecision FromProto(Api.CheckDecision p) => p switch
    {
        Api.CheckDecision.Allow => Abstractions.CheckDecision.Allow,
        Api.CheckDecision.Conditional => Abstractions.CheckDecision.Conditional,
        _ => Abstractions.CheckDecision.Deny,
    };

    /// <summary>Converts a domain <see cref="Abstractions.UnmetCondition"/> to its proto form.</summary>
    public static Api.UnmetCondition ToProto(Abstractions.UnmetCondition u)
    {
        var proto = new Api.UnmetCondition { Condition = u.Condition };
        proto.MissingKeys.AddRange(u.MissingKeys);
        return proto;
    }

    /// <summary>Converts a proto <see cref="Api.UnmetCondition"/> to its domain form.</summary>
    public static Abstractions.UnmetCondition FromProto(Api.UnmetCondition p) =>
        new(p.Condition, [.. p.MissingKeys]);

    /// <summary>Converts a domain <see cref="Abstractions.MetricsSnapshot"/> to its proto form.</summary>
    public static Api.MetricsSnapshot ToProto(Abstractions.MetricsSnapshot s) => new()
    {
        CapturedAt = Timestamp.FromDateTimeOffset(s.CapturedAt),
        CheckCount = s.CheckCount,
        CheckP50Ms = s.CheckP50Ms,
        CheckP95Ms = s.CheckP95Ms,
        CheckP99Ms = s.CheckP99Ms,
        CacheHits = s.CacheHits,
        CacheMisses = s.CacheMisses,
        CacheSwept = s.CacheSwept,
    };

    /// <summary>Converts a proto <see cref="Api.MetricsSnapshot"/> to its domain form. A missing <c>captured_at</c> decodes to the Unix epoch.</summary>
    public static Abstractions.MetricsSnapshot FromProto(Api.MetricsSnapshot p) => new(
        p.CapturedAt is null ? DateTimeOffset.UnixEpoch : p.CapturedAt.ToDateTimeOffset(),
        p.CheckCount,
        p.CheckP50Ms,
        p.CheckP95Ms,
        p.CheckP99Ms,
        p.CacheHits,
        p.CacheMisses,
        p.CacheSwept);

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
