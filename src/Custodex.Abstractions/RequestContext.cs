namespace Custodex.Abstractions;

public sealed record RequestContext(
    DateTimeOffset Now,
    SubjectRef Subject,
    IReadOnlyDictionary<string, object?> Attributes);
