using Relkit.Abstractions;

namespace Relkit.Core;

public sealed class SubjectFillerBuilder
{
    private readonly List<SubjectTypeRef> _fillers = [];
    public SubjectFillerBuilder User() { _fillers.Add(new SubjectTypeRef("user")); return this; }
    public SubjectFillerBuilder Type(string type) { _fillers.Add(new SubjectTypeRef(type)); return this; }
    public SubjectFillerBuilder SubjectSet(string type, string relation) { _fillers.Add(new SubjectTypeRef(type, relation)); return this; }
    public SubjectFillerBuilder Wildcard(string type) { _fillers.Add(new SubjectTypeRef(type, null, true)); return this; }
    public IReadOnlyList<SubjectTypeRef> Build() => _fillers;
}
