using Custodex.Abstractions;

namespace Custodex.Core;

public sealed class ConditionParamBuilder
{
    private readonly List<ConditionParam> _params = [];
    private ConditionParamBuilder Add(string name, ConditionType type) { _params.Add(new ConditionParam(name, type)); return this; }
    public ConditionParamBuilder Bool(string name) => Add(name, ConditionType.Bool);
    public ConditionParamBuilder Int(string name) => Add(name, ConditionType.Int);
    public ConditionParamBuilder Long(string name) => Add(name, ConditionType.Long);
    public ConditionParamBuilder Double(string name) => Add(name, ConditionType.Double);
    public ConditionParamBuilder String(string name) => Add(name, ConditionType.String);
    public ConditionParamBuilder Timestamp(string name) => Add(name, ConditionType.Timestamp);
    public IReadOnlyList<ConditionParam> Build() => _params;
}
