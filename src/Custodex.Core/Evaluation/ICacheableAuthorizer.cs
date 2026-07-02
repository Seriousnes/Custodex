using Custodex.Abstractions;

namespace Custodex.Core.Evaluation;

internal interface ICacheableAuthorizer : IAuthorizer
{
    Task<(CheckResult Result, bool ConditionTouched)> CheckInternalAsync(CheckRequest request, CancellationToken ct = default);
}
