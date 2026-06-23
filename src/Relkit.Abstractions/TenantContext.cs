namespace Relkit.Abstractions;

public readonly record struct TenantContext(string Store, string Tenant);
