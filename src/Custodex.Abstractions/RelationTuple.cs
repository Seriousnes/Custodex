namespace Custodex.Abstractions;

/// <summary>
/// Binds a named condition to the tuple that carries it, together with the parameter values fixed at
/// write time. At decision time the condition is re-evaluated against these parameters plus the
/// request's attributes; a tuple whose condition does not pass does not contribute to a grant.
/// </summary>
/// <param name="Name">The condition's name as declared in the schema.</param>
/// <param name="Parameters">Parameter values bound to the condition for this tuple, keyed by parameter name.</param>
public sealed record ConditionRef(string Name, IReadOnlyDictionary<string, object?> Parameters);

/// <summary>
/// A single authorization fact: <paramref name="Subject"/> holds <paramref name="Relation"/> on
/// <paramref name="Object"/>. Tuples are the runtime-editable data the engine traverses; an optional
/// <paramref name="Condition"/> makes the grant contingent on a condition passing. The owning
/// <see cref="TenantContext"/> is supplied by the operation, not stored on the tuple, so the same
/// tuple value is reusable across stores.
/// </summary>
/// <param name="Object">The object the relation is held on.</param>
/// <param name="Relation">The relation name, as declared on the object's type in the schema.</param>
/// <param name="Subject">The subject (or subject set) holding the relation.</param>
/// <param name="Condition">An optional condition guarding the grant; <see langword="null"/> for an unconditional fact.</param>
public sealed record RelationTuple(
    EntityRef Object,
    string Relation,
    SubjectRef Subject,
    ConditionRef? Condition = null);
