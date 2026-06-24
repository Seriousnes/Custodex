namespace Custodex.Storage.Postgres.Tests.Spike;

/// <summary>
/// Throwaway spike fixtures. Two cases interleave algebra WITH arrow traversal — the shape
/// spec §7.1 calls out as the hardest. Tuples are raw column rows (the spike writes them with
/// Dapper, bypassing the not-yet-built relation store). Expected answers are hand-computed and
/// match what the m0/05 EngineDrivenAuthorizer returns for the same data.
/// </summary>
internal static class SpikeData
{
    internal const string Store = "spike";
    internal const string Tenant = "t";

    /// <summary>(object_type, object_id, relation, subject_type, subject_id, subject_relation?)</summary>
    internal sealed record Fact(
        string ObjectType, string ObjectId, string Relation,
        string SubjectType, string SubjectId, string? SubjectRelation = null);

    /// <summary>
    /// Case A: <c>animal.edit = enclosure-&gt;edit</c>; <c>enclosure.edit = editor - blocked</c>.
    /// carol is both editor AND blocked on KH1, so the inner exclusion under the arrow denies her.
    /// </summary>
    internal static readonly IReadOnlyList<Fact> CaseA =
    [
        new("animal", "EL-001", "enclosure", "enclosure", "KH1"),
        new("enclosure", "KH1", "editor", "user", "carol"),
        new("enclosure", "KH1", "blocked", "user", "carol"),
        new("enclosure", "KH1", "editor", "user", "dana"),
    ];

    /// <summary>Hand-computed truth for <c>animal:EL-001#edit</c> (Case A).</summary>
    internal static readonly IReadOnlyDictionary<(string Obj, string Subject), bool> CaseA_Expected =
        new Dictionary<(string, string), bool>
        {
            [("EL-001", "carol")] = false,
            [("EL-001", "dana")] = true,
        };

    /// <summary>
    /// Case B (spec §12.5): <c>animal.access = enclosure-&gt;is_quarantine &amp; vet_member &amp; trained_member</c>.
    /// Q1 carries <c>is_quarantine@user:*</c>, making the arrow gate universal, so access reduces to vet ∧ trained.
    /// </summary>
    internal static readonly IReadOnlyList<Fact> CaseB =
    [
        new("animal", "EL-001", "enclosure", "enclosure", "Q1"),
        new("enclosure", "Q1", "is_quarantine", "user", "*"),
        new("animal", "EL-001", "vet_member", "group", "vets", "member"),
        new("animal", "EL-001", "trained_member", "group", "trained", "member"),
        new("group", "vets", "member", "user", "dr-smith"),
        new("group", "vets", "member", "user", "jones"),
        new("group", "trained", "member", "user", "dr-smith"),
    ];

    /// <summary>Hand-computed truth for <c>animal:EL-001#access</c> (Case B / §12.5).</summary>
    internal static readonly IReadOnlyDictionary<(string Obj, string Subject), bool> CaseB_Expected =
        new Dictionary<(string, string), bool>
        {
            [("EL-001", "dr-smith")] = true,
            [("EL-001", "jones")] = false,
            [("EL-001", "outsider")] = false,
        };
}
