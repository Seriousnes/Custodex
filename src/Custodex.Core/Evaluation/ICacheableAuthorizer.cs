using Custodex.Abstractions;

namespace Custodex.Core.Evaluation;

/// <summary>
/// Internal seam exposing the richer (decision + condition-touched) Check result that
/// the m0/08 caching decorator needs but the public IAuthorizer cannot express. Both the
/// engine-driven authorizer and (M1) the CTE authorizer implement it, so CachingAuthorizer
/// can wrap either.
/// </summary>
internal interface ICacheableAuthorizer : IAuthorizer
{
    Task<(bool Allowed, bool ConditionTouched)> CheckInternalAsync(CheckRequest request, CancellationToken ct = default);
}
