using Custodex.Abstractions;

namespace Custodex.Core;

/// <summary>Fluent builder for the subject shapes a relation accepts: subject types, subject sets, and the type-wide wildcard.</summary>
public sealed class SubjectFillerBuilder
{
    private readonly List<SubjectTypeRef> _fillers = [];

    /// <summary>Accepts subjects of the type named <c>user</c>, a shorthand for <c>Type("user")</c>.</summary>
    /// <returns>This builder, for chaining.</returns>
    public SubjectFillerBuilder User() { _fillers.Add(new SubjectTypeRef("user")); return this; }

    /// <summary>Accepts any subject of the given entity type.</summary>
    /// <param name="type">The permitted subject entity type.</param>
    /// <returns>This builder, for chaining.</returns>
    public SubjectFillerBuilder Type(string type) { _fillers.Add(new SubjectTypeRef(type)); return this; }

    /// <summary>Accepts subject sets <c>type:id#relation</c>, granting to whoever holds <paramref name="relation"/> on a subject of <paramref name="type"/>.</summary>
    /// <param name="type">The subject entity type whose members are referenced.</param>
    /// <param name="relation">The relation on that subject whose holders are accepted.</param>
    /// <returns>This builder, for chaining.</returns>
    public SubjectFillerBuilder SubjectSet(string type, string relation) { _fillers.Add(new SubjectTypeRef(type, relation)); return this; }

    /// <summary>Accepts the type-wide wildcard <c>type:*</c>, granting to every subject of the type.</summary>
    /// <param name="type">The subject entity type the wildcard covers.</param>
    /// <returns>This builder, for chaining.</returns>
    public SubjectFillerBuilder Wildcard(string type) { _fillers.Add(new SubjectTypeRef(type, null, true)); return this; }

    /// <summary>Produces the accumulated list of accepted subject shapes.</summary>
    /// <returns>The declared subject shapes.</returns>
    public IReadOnlyList<SubjectTypeRef> Build() => _fillers;
}
