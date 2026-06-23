using Relkit.Abstractions;

namespace Relkit.Core.Evaluation;

public sealed partial class EngineDrivenAuthorizer
{
    public Task<IReadOnlyList<CheckResult>> BatchCheckAsync(BatchCheckRequest request, CancellationToken ct = default)
        => throw new NotImplementedException("Implemented in m0/07.");
}
