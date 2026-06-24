namespace Custodex.Storage.Postgres.Tests.Spike;

/// <summary>
/// Throwaway spike fixtures. Two cases interleave algebra WITH arrow traversal — the hardest
/// shape. Tuples are raw column rows (the spike writes them with Dapper, bypassing the
/// not-yet-built relation store). Expected answers are hand-computed and match what the
/// EngineDrivenAuthorizer returns for the same data.
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
    /// Case A: <c>doc.edit = folder-&gt;edit</c>; <c>folder.edit = editor - blocked</c>.
    /// carol is both editor AND blocked on KH1, so the inner exclusion under the arrow denies her.
    /// </summary>
    internal static readonly IReadOnlyList<Fact> CaseA =
    [
        new("doc", "D1", "folder", "folder", "KH1"),
        new("folder", "KH1", "editor", "user", "carol"),
        new("folder", "KH1", "blocked", "user", "carol"),
        new("folder", "KH1", "editor", "user", "dana"),
    ];

    /// <summary>Hand-computed truth for <c>doc:D1#edit</c> (Case A).</summary>
    internal static readonly IReadOnlyDictionary<(string Obj, string Subject), bool> CaseA_Expected =
        new Dictionary<(string, string), bool>
        {
            [("D1", "carol")] = false,
            [("D1", "dana")] = true,
        };

    /// <summary>
    /// Case B: <c>doc.access = folder-&gt;is_quarantine &amp; vet_member &amp; trained_member</c>.
    /// Q1 carries <c>is_quarantine@user:*</c>, making the arrow gate universal, so access reduces to vet_member ∧ trained.
    /// </summary>
    internal static readonly IReadOnlyList<Fact> CaseB =
    [
        new("doc", "D1", "folder", "folder", "Q1"),
        new("folder", "Q1", "is_quarantine", "user", "*"),
        new("doc", "D1", "vet_member", "group", "reds", "member"),
        new("doc", "D1", "trained_member", "group", "trained", "member"),
        new("group", "reds", "member", "user", "pat"),
        new("group", "reds", "member", "user", "jones"),
        new("group", "trained", "member", "user", "pat"),
    ];

    /// <summary>Hand-computed truth for <c>doc:D1#access</c> (Case B).</summary>
    internal static readonly IReadOnlyDictionary<(string Obj, string Subject), bool> CaseB_Expected =
        new Dictionary<(string, string), bool>
        {
            [("D1", "pat")] = true,
            [("D1", "jones")] = false,
            [("D1", "outsider")] = false,
        };
}
