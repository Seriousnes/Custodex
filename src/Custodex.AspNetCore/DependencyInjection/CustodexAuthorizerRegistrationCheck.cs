using Microsoft.Extensions.Hosting;

namespace Custodex.AspNetCore;

internal sealed class CustodexAuthorizerRegistrationCheck(IServiceProvider services) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        CustodexAuthorizationValidation.EnsureAuthorizerRegistered(services);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
