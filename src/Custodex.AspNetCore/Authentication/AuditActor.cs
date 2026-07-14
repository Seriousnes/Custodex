using System.Security.Claims;

namespace Custodex.AspNetCore;

internal static class AuditActor
{
    private const string Unknown = "unknown";

    internal static string From(ClaimsPrincipal? user)
    {
        var id = user?.FindFirstValue(ClaimTypes.NameIdentifier);
        return string.IsNullOrEmpty(id) ? Unknown : id;
    }
}
