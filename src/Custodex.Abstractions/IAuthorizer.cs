namespace Custodex.Abstractions;

/// <summary>
/// The engine's read-side decision API. Every operation evaluates the active schema's permission
/// expressions over the stored tuples and attributes for the request's tenant, and returns its
/// decision as a value — only a malformed schema or an exceeded evaluation limit surfaces as an exception.
/// </summary>
public interface IAuthorizer
{
    /// <summary>Decides whether a single subject holds a permission on a single object.</summary>
    /// <param name="request">The object, permission, subject, and request context to evaluate.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    /// <returns>The decision, optionally carrying an explain trace when one was requested.</returns>
    Task<CheckResult> CheckAsync(CheckRequest request, CancellationToken ct = default);

    /// <summary>Decides many object/permission/subject triples in one request, sharing evaluation work.</summary>
    /// <param name="request">The batch of items and the shared request context.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    /// <returns>One result per item, in request order.</returns>
    Task<IReadOnlyList<CheckResult>> BatchCheckAsync(BatchCheckRequest request, CancellationToken ct = default);

    /// <summary>Lists the objects of a type on which a subject holds a permission (the forward query).</summary>
    /// <param name="request">The subject, object type, permission, and pagination cursor.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    /// <returns>A page of object ids and a continuation token when more remain.</returns>
    Task<ListObjectsResult> ListObjectsAsync(ListObjectsRequest request, CancellationToken ct = default);

    /// <summary>Lists the subjects that hold a permission on an object (the reverse query).</summary>
    /// <param name="request">The object, permission, and pagination cursor.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    /// <returns>A page of subjects and a continuation token when more remain.</returns>
    Task<ListSubjectsResult> ListSubjectsAsync(ListSubjectsRequest request, CancellationToken ct = default);
}
