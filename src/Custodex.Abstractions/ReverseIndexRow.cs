namespace Custodex.Abstractions;

/// <summary>
/// One maintained reverse-index row: <paramref name="Subject"/> (a canonical SubjectRef string)
/// holds <paramref name="Permission"/> on <paramref name="ObjectType"/>:<paramref name="ObjectId"/>
/// structurally. When <paramref name="Conditioned"/> is true, a request-time condition was reached
/// on the grant path and must be re-evaluated before the grant is honoured.
/// The (store, tenant, schema_version) scope is supplied alongside the row, not stored on it.
/// </summary>
public sealed record ReverseIndexRow(
    string Subject,
    string Permission,
    string ObjectType,
    string ObjectId,
    bool Conditioned);
