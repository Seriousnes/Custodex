using Microsoft.AspNetCore.Authorization;

namespace Custodex.AspNetCore;

internal sealed class CustodexRequirement(string objectType, string permission) : IAuthorizationRequirement
{
    public string ObjectType { get; } = objectType;

    public string Permission { get; } = permission;
}
