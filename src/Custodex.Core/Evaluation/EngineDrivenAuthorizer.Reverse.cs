using Custodex.Abstractions;

namespace Custodex.Core.Evaluation;

public sealed partial class EngineDrivenAuthorizer
{
    internal Task<IReadOnlyList<EntityRef>> CandidateObjectsForTest(
        TenantContext tenant, SubjectRef subject, string objectType, CancellationToken ct = default)
        => CandidateObjectsAsync(tenant, subject, objectType, ct);

    private async Task<IReadOnlyList<EntityRef>> CandidateObjectsAsync(
        TenantContext tenant, SubjectRef subject, string objectType, CancellationToken ct)
    {
        var found = new SortedSet<string>(StringComparer.Ordinal);
        var visitedSubjects = new HashSet<SubjectRef>();
        var visitedObjects = new HashSet<EntityRef>();

        var subjectQueue = new Queue<SubjectRef>();
        subjectQueue.Enqueue(subject);

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

                subjectQueue.Enqueue(new SubjectRef(obj.Type, obj.Id, tuple.Relation));

                if (visitedObjects.Add(obj)) objectQueue.Enqueue(obj);
            }
        }

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
