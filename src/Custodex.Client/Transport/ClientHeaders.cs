using Custodex.Abstractions;

using Grpc.Core;

namespace Custodex.Client.Transport;

internal static class ClientHeaders
{
    internal const string TenantHeader = "x-custodex-tenant";

    internal static Metadata TenantMeta(TenantContext tenant) =>
        new() { { TenantHeader, tenant.Tenant } };
}
