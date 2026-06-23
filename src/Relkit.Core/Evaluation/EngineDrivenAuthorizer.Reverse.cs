using Relkit.Abstractions;

namespace Relkit.Core.Evaluation;

public sealed partial class EngineDrivenAuthorizer
{
    internal Task<IReadOnlyList<EntityRef>> CandidateObjectsForTest(
        TenantContext tenant, SubjectRef subject, string objectType, CancellationToken ct = default)
        => CandidateObjectsAsync(tenant, subject, objectType, ct);

    /// <summary>
    /// Reverse traversal: the distinct, ordinal-id-sorted objects of
    /// <paramref name="objectType"/> reachable from <paramref name="subject"/> by
    /// following the subject's tuples and the tuples of every group it transitively
    /// belongs to, plus structural-reference edges into objects of the target type.
    /// A complete superset of the ListObjects answer; each is later confirmed by Check.
    /// </summary>
    private async Task<IReadOnlyList<EntityRef>> CandidateObjectsAsync(
        TenantContext tenant, SubjectRef subject, string objectType, CancellationToken ct)
    {
        var found = new SortedSet<string>(StringComparer.Ordinal);
        var visitedSubjects = new HashSet<SubjectRef>();
        var visitedObjects = new HashSet<EntityRef>();

        // Frontier of "principals" whose inbound tuples we follow: the subject and
        // every group-as-member it expands into.
        var subjectQueue = new Queue<SubjectRef>();
        subjectQueue.Enqueue(subject);

        // Objects discovered via structural edges that we still need to walk through.
        var objectQueue = new Queue<EntityRef>();

        while (subjectQueue.Count > 0)
        {
            var principal = subjectQueue.Dequeue();
            if (!visitedSubjects.Add(principal)) continue;

            var inbound = await _relations.GetBySubjectAsync(tenant, principal, ct);
            foreach (var tuple in inbound)
            {
                var obj = tuple.Object;

                if (string.Equals(obj.Type, objectType, StringComparison.Ordinal))
                    found.Add(obj.Id);

                // The principal fills obj#relation; the set obj:id#relation may be
                // referenced as a subject-set anywhere in the graph, so climb to it for
                // every inbound tuple (generalizes the original group#member-only climb).
                // Check confirms arbitrary subject-sets, so candidate gen must follow them
                // all; the visited-set guard keeps this terminating, and a climb into a set
                // that is never referenced as a subject simply finds nothing.
                subjectQueue.Enqueue(new SubjectRef(obj.Type, obj.Id, tuple.Relation));

                // Any object reached is a potential start for structural-edge walks.
                if (visitedObjects.Add(obj)) objectQueue.Enqueue(obj);
            }
        }

        // Walk structural edges (e.g. enclosure -> animal) so arrow-inherited grants
        // surface candidates of the target type. We discover objects whose tuples point
        // *at* an already-found object, climbing one structural hop at a time.
        while (objectQueue.Count > 0)
        {
            var obj = objectQueue.Dequeue();
            var asSubject = new SubjectRef(obj.Type, obj.Id);
            if (!visitedSubjects.Add(asSubject)) continue;

            var inbound = await _relations.GetBySubjectAsync(tenant, asSubject, ct);
            foreach (var tuple in inbound)
            {
                var owner = tuple.Object;
                if (string.Equals(owner.Type, objectType, StringComparison.Ordinal))
                    found.Add(owner.Id);
                if (visitedObjects.Add(owner)) objectQueue.Enqueue(owner);
            }
        }

        return found.Select(id => new EntityRef(objectType, id)).ToList();
    }
}
