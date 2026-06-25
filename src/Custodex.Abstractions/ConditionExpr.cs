namespace Custodex.Abstractions;

/// <summary>
/// The base of the condition-body (ABAC) expression AST. Concrete nodes form a closed set the condition
/// evaluator switches over to compute a boolean from a condition's parameters and the request attributes.
/// </summary>
public abstract record ConditionExpr;
