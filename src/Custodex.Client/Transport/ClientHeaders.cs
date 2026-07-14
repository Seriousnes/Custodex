using Custodex.Abstractions;

using Grpc.Core;

namespace Custodex.Client.Transport;

internal static class ClientHeaders
{
    internal const string TenantHeader = "x-custodex-tenant";

    internal const string StoreHeader = "x-custodex-store";

    internal static Metadata TenantMeta(TenantContext tenant) =>
        new() { { TenantHeader, tenant.Tenant }, { StoreHeader, tenant.Store } };

    internal static Metadata StoreMeta(string store) =>
        new() { { StoreHeader, store } };
}
