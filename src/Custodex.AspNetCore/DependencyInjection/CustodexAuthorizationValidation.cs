using Custodex.Abstractions;

using Microsoft.Extensions.DependencyInjection;

namespace Custodex.AspNetCore;

internal static class CustodexAuthorizationValidation
{
    public static void EnsureAuthorizerRegistered(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        if (scope.ServiceProvider.GetService<IAuthorizer>() is null)
            throw new InvalidOperationException(
                "Custodex authorization requires an IAuthorizer in DI. Register the engine with " +
                "AddCustodex().Use<provider>() or the remote client with AddCustodexClient(...) before AddCustodexAuthorization().");
    }
}
