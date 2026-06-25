namespace Custodex.Abstractions;

/// <summary>Thrown when a <see cref="Schema"/> fails validation before activation.</summary>
/// <param name="errors">The validation error messages.</param>
public sealed class SchemaValidationException(IReadOnlyList<string> errors)
    : Exception("Schema validation failed: " + string.Join("; ", errors))
{
    /// <summary>The validation errors that caused the failure.</summary>
    public IReadOnlyList<string> Errors { get; } = errors;
}

/// <summary>Thrown when a request names an entity type the active schema does not declare.</summary>
/// <param name="type">The unknown type name.</param>
public sealed class UnknownTypeException(string type) : Exception($"Unknown entity type '{type}'.")
{
    /// <summary>The unknown type name.</summary>
    public string Type { get; } = type;
}

/// <summary>Thrown when a request names a relation the type does not declare.</summary>
/// <param name="type">The type the relation was expected on.</param>
/// <param name="relation">The unknown relation name.</param>
public sealed class UnknownRelationException(string type, string relation)
    : Exception($"Unknown relation '{relation}' on type '{type}'.")
{
    /// <summary>The type the relation was expected on.</summary>
    public string Type { get; } = type;

    /// <summary>The unknown relation name.</summary>
    public string Relation { get; } = relation;
}

/// <summary>Thrown when a request names a permission the type does not declare.</summary>
/// <param name="type">The type the permission was expected on.</param>
/// <param name="permission">The unknown permission name.</param>
public sealed class UnknownPermissionException(string type, string permission)
    : Exception($"Unknown permission '{permission}' on type '{type}'.")
{
    /// <summary>The type the permission was expected on.</summary>
    public string Type { get; } = type;

    /// <summary>The unknown permission name.</summary>
    public string Permission { get; } = permission;
}

/// <summary>Thrown when evaluation exceeds a safety limit, such as the recursion-depth bound.</summary>
/// <param name="detail">A description of the limit that was reached.</param>
public sealed class EvaluationLimitException(string detail) : Exception(detail);
