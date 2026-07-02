namespace Custodex.Abstractions;

/// <summary>
/// The per-request ambient inputs to an authorization decision: the clock, the subject the decision is
/// about, and the attributes condition expressions evaluate against. Supplying time here — rather than
/// reading it inside the engine — keeps evaluation deterministic and reproducible.
/// </summary>
/// <param name="Now">The instant the decision is evaluated at; the only source of time a condition may read.</param>
/// <param name="Subject">The subject the decision concerns.</param>
/// <param name="Attributes">Request-scoped attribute values exposed to condition (ABAC) evaluation.</param>
/// <param name="Consistency">The consistency level the read requests; <see langword="null"/> is equivalent to <see cref="Consistency.MinimizeLatency"/>.</param>
public sealed record RequestContext(
    DateTimeOffset Now,
    SubjectRef Subject,
    IReadOnlyDictionary<string, object?> Attributes,
    Consistency? Consistency = null);
