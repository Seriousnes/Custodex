namespace Relkit.Abstractions;

public sealed class SchemaValidationException(IReadOnlyList<string> errors)
    : Exception("Schema validation failed: " + string.Join("; ", errors))
{ public IReadOnlyList<string> Errors { get; } = errors; }

public sealed class UnknownTypeException(string type) : Exception($"Unknown entity type '{type}'.")
{ public string Type { get; } = type; }

public sealed class UnknownRelationException(string type, string relation)
    : Exception($"Unknown relation '{relation}' on type '{type}'.")
{ public string Type { get; } = type; public string Relation { get; } = relation; }

public sealed class UnknownPermissionException(string type, string permission)
    : Exception($"Unknown permission '{permission}' on type '{type}'.")
{ public string Type { get; } = type; public string Permission { get; } = permission; }

public sealed class EvaluationLimitException(string detail) : Exception(detail);
