using Custodex.Abstractions;

namespace Custodex.Core;

/// <summary>Fluent builder for the typed parameters a condition declares, bound by tuples and read by its body.</summary>
public sealed class ConditionParamBuilder
{
    private readonly List<ConditionParam> _params = [];
    private ConditionParamBuilder Add(string name, ConditionType type) { _params.Add(new ConditionParam(name, type)); return this; }

    /// <summary>Declares a boolean parameter.</summary>
    /// <param name="name">The parameter name.</param>
    /// <returns>This builder, for chaining.</returns>
    public ConditionParamBuilder Bool(string name) => Add(name, ConditionType.Bool);

    /// <summary>Declares a 32-bit integer parameter.</summary>
    /// <param name="name">The parameter name.</param>
    /// <returns>This builder, for chaining.</returns>
    public ConditionParamBuilder Int(string name) => Add(name, ConditionType.Int);

    /// <summary>Declares a 64-bit integer parameter.</summary>
    /// <param name="name">The parameter name.</param>
    /// <returns>This builder, for chaining.</returns>
    public ConditionParamBuilder Long(string name) => Add(name, ConditionType.Long);

    /// <summary>Declares a floating-point parameter.</summary>
    /// <param name="name">The parameter name.</param>
    /// <returns>This builder, for chaining.</returns>
    public ConditionParamBuilder Double(string name) => Add(name, ConditionType.Double);

    /// <summary>Declares a string parameter.</summary>
    /// <param name="name">The parameter name.</param>
    /// <returns>This builder, for chaining.</returns>
    public ConditionParamBuilder String(string name) => Add(name, ConditionType.String);

    /// <summary>Declares a timestamp parameter.</summary>
    /// <param name="name">The parameter name.</param>
    /// <returns>This builder, for chaining.</returns>
    public ConditionParamBuilder Timestamp(string name) => Add(name, ConditionType.Timestamp);

    /// <summary>Produces the accumulated list of declared parameters.</summary>
    /// <returns>The declared parameters.</returns>
    public IReadOnlyList<ConditionParam> Build() => _params;
}
