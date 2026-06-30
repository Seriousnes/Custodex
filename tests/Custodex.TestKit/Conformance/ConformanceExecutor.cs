using Custodex.Abstractions;

namespace Custodex.TestKit.Conformance;

/// <summary>Thrown when an execution path's answer disagrees with a scenario's hand-asserted expectation.</summary>
public sealed class ConformanceException(string scenario, string assertion, string detail)
    : Exception($"Conformance scenario '{scenario}', assertion '{assertion}': {detail}");

/// <summary>
/// Runs every Check, ListObjects, and ListSubjects expectation of a <see cref="ConformanceScenario"/>
/// against an already-seeded <see cref="IAuthorizer"/> and throws a <see cref="ConformanceException"/>
/// on the first mismatch. Used identically by every path so each is checked against the same ground truth.
/// List results are drained across all pages at a small page size, so pagination is exercised too.
/// </summary>
public static class ConformanceExecutor
{
    private static readonly IReadOnlyDictionary<string, object?> EmptyContext =
        new Dictionary<string, object?>(StringComparer.Ordinal);

    /// <summary>Asserts every expectation in <paramref name="scenario"/> against <paramref name="authorizer"/>,
    /// which must already have the scenario's schema, tuples, and attributes seeded under <paramref name="tenant"/>.</summary>
    public static async Task AssertAsync(
        IAuthorizer authorizer, TenantContext tenant, ConformanceScenario scenario, CancellationToken ct = default)
    {
        foreach (var c in scenario.Checks)
        {
            var ctx = new RequestContext(c.Now ?? DateTimeOffset.UnixEpoch, c.Subject, c.Context ?? EmptyContext);
            var result = await authorizer.CheckAsync(
                new CheckRequest(tenant, c.Object, c.Permission, c.Subject, ctx), ct);
            if (result.Allowed != c.Expected)
                throw new ConformanceException(scenario.Name, c.Name,
                    $"Check {c.Subject} on {c.Object}#{c.Permission}: expected Allowed={c.Expected}, got {result.Allowed}.");
        }

        foreach (var l in scenario.ListObjects)
        {
            var actual = await DrainObjectsAsync(authorizer, tenant, l, ct);
            var expected = new HashSet<string>(l.ExpectedIds, StringComparer.Ordinal);
            if (!actual.SetEquals(expected))
                throw new ConformanceException(scenario.Name, l.Name,
                    $"ListObjects {l.Subject} on {l.ObjectType}#{l.Permission}: " +
                    $"expected [{Render(expected)}], got [{Render(actual)}].");
        }

        foreach (var l in scenario.ListSubjects)
        {
            var actual = await DrainSubjectsAsync(authorizer, tenant, l, ct);
            var expected = new HashSet<string>(l.ExpectedSubjects.Select(s => s.ToString()), StringComparer.Ordinal);
            if (!actual.SetEquals(expected))
                throw new ConformanceException(scenario.Name, l.Name,
                    $"ListSubjects on {l.Object}#{l.Permission}: " +
                    $"expected [{Render(expected)}], got [{Render(actual)}].");
        }
    }

    private static async Task<HashSet<string>> DrainObjectsAsync(
        IAuthorizer auth, TenantContext tenant, ListObjectsExpectation l, CancellationToken ct)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        string? token = null;
        do
        {
            var ctx = new RequestContext(l.Now ?? DateTimeOffset.UnixEpoch, l.Subject, l.Context ?? EmptyContext);
            var page = await auth.ListObjectsAsync(
                new ListObjectsRequest(tenant, l.Subject, l.ObjectType, l.Permission, ctx, PageSize: 2, ContinuationToken: token), ct);
            foreach (var id in page.ObjectIds) ids.Add(id);
            token = page.ContinuationToken;
        } while (token is not null);
        return ids;
    }

    private static async Task<HashSet<string>> DrainSubjectsAsync(
        IAuthorizer auth, TenantContext tenant, ListSubjectsExpectation l, CancellationToken ct)
    {
        var subjects = new HashSet<string>(StringComparer.Ordinal);
        string? token = null;
        do
        {
            var ctx = new RequestContext(l.Now ?? DateTimeOffset.UnixEpoch, new SubjectRef("user", "system"), l.Context ?? EmptyContext);
            var page = await auth.ListSubjectsAsync(
                new ListSubjectsRequest(tenant, l.Object, l.Permission, ctx, PageSize: 2, ContinuationToken: token), ct);
            foreach (var s in page.Subjects) subjects.Add(s.ToString());
            token = page.ContinuationToken;
        } while (token is not null);
        return subjects;
    }

    private static string Render(IEnumerable<string> xs) =>
        string.Join(", ", xs.OrderBy(x => x, StringComparer.Ordinal));
}
