using Custodex.Abstractions;

namespace Custodex.AspNetCore;

internal sealed class DefaultRequestContextFactory(TimeProvider timeProvider, IEnumerable<ICustodexAttributeSource> sources) : IRequestContextFactory
{
    public RequestContext Create(SubjectRef subject, CustodexResolutionContext context)
    {
        var attributes = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var source in sources)
            source.Contribute(attributes, context);

        return new RequestContext(timeProvider.GetUtcNow(), subject, attributes);
    }
}
