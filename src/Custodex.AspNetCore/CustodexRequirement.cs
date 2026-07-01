using Microsoft.AspNetCore.Authorization;

namespace Custodex.AspNetCore;

internal sealed class CustodexRequirement(string objectType, string permission, bool anyObject = false) : IAuthorizationRequirement
{
    public string ObjectType { get; } = objectType;

    public string Permission { get; } = permission;

    public bool AnyObject { get; } = anyObject;
}
