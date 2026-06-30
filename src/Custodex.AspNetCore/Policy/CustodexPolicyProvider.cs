using System.Collections.Concurrent;

using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;

namespace Custodex.AspNetCore;

internal sealed class CustodexPolicyProvider : IAuthorizationPolicyProvider
{
    private readonly DefaultAuthorizationPolicyProvider _inner;
    private readonly CustodexAuthorizationOptions _options;
    private readonly ConcurrentDictionary<string, AuthorizationPolicy> _cache = new(StringComparer.Ordinal);

    public CustodexPolicyProvider(
        IOptions<AuthorizationOptions> authorizationOptions,
        IOptions<CustodexAuthorizationOptions> options)
    {
        _inner = new DefaultAuthorizationPolicyProvider(authorizationOptions);
        _options = options.Value;
    }

    public Task<AuthorizationPolicy> GetDefaultPolicyAsync() => _inner.GetDefaultPolicyAsync();

    public Task<AuthorizationPolicy?> GetFallbackPolicyAsync() => _inner.GetFallbackPolicyAsync();

    public Task<AuthorizationPolicy?> GetPolicyAsync(string policyName)
    {
        if (TryParse(policyName, out var type, out var permission))
        {
            var policy = _cache.GetOrAdd(policyName, _ => new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .AddRequirements(new CustodexRequirement(type, permission))
                .Build());
            return Task.FromResult<AuthorizationPolicy?>(policy);
        }

        return _inner.GetPolicyAsync(policyName);
    }

    private bool TryParse(string name, out string type, out string permission)
    {
        type = string.Empty;
        permission = string.Empty;

        var prefix = _options.PolicyPrefix + _options.PolicySeparator;
        if (!name.StartsWith(prefix, StringComparison.Ordinal))
            return false;

        var parts = name.Split(_options.PolicySeparator);
        if (parts.Length != 3 || parts[0] != _options.PolicyPrefix || parts[1].Length == 0 || parts[2].Length == 0)
            return false;

        type = parts[1];
        permission = parts[2];
        return true;
    }
}
