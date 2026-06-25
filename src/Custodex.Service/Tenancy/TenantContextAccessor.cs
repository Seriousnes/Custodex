using Custodex.Abstractions;

namespace Custodex.Service.Tenancy;

internal sealed class TenantContextAccessor : ITenantContextAccessor
{
    internal TenantContext? Value { get; set; }

    internal string? Store { get; set; }

    public TenantContext Current => Value ?? throw new MissingTenantContextException();

    public string? AuthenticatedStore => Store;
}
