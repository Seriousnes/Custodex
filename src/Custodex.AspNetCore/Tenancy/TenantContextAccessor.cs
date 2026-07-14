using Custodex.Abstractions;

namespace Custodex.AspNetCore;

internal sealed class TenantContextAccessor : ITenantContextAccessor
{
    internal TenantContext? Value { get; set; }

    internal string? Store { get; set; }

    internal bool Operator { get; set; }

    public TenantContext Current => Value ?? throw new MissingTenantContextException();

    public string? AuthenticatedStore => Store;

    public bool IsOperator => Operator;
}
