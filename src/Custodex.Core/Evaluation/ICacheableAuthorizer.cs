using Custodex.Abstractions;

namespace Custodex.Core.Evaluation;

internal interface ICacheableAuthorizer : IAuthorizer
{
    Task<(bool Allowed, bool ConditionTouched)> CheckInternalAsync(CheckRequest request, CancellationToken ct = default);
}
