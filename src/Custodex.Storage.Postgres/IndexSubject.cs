using Custodex.Abstractions;

namespace Custodex.Storage.Postgres;

/// <summary>
/// Canonical <c>reverse_index.subject</c> encoding shared between index maintenance and index
/// reads, ensuring that subject strings written during maintenance match the keys used during scans
/// exactly: <c>type:id</c>, <c>type:id#relation</c> for a subject-set, <c>type:*</c> for a wildcard.
/// </summary>
public static class IndexSubject
{
    /// <summary>
    /// Returns the canonical <c>reverse_index.subject</c> string for <paramref name="subject"/>.
    /// </summary>
    public static string Of(SubjectRef subject) => subject.ToString();
}
