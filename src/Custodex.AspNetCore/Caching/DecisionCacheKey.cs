using System.Globalization;
using System.Text;

using Custodex.Abstractions;

namespace Custodex.AspNetCore;

internal static class DecisionCacheKey
{
    private const char Sep = '';

    public static string Build(
        TenantContext tenant, string schemaVersion, EntityRef obj, string permission, SubjectRef subject,
        string? contextFingerprint)
    {
        var subj = subject.Relation is null
            ? $"{subject.Type}:{subject.Id}"
            : $"{subject.Type}:{subject.Id}#{subject.Relation}";
        return string.Join(Sep,
            "Custodex.decision",
            tenant.Store,
            tenant.Tenant,
            schemaVersion,
            $"{obj.Type}:{obj.Id}",
            permission,
            subj,
            contextFingerprint ?? "");
    }

    public static string Fingerprint(RequestContext context)
    {
        var sb = new StringBuilder();
        sb.Append(context.Now.UtcTicks.ToString(CultureInfo.InvariantCulture));
        foreach (var pair in context.Attributes.OrderBy(static p => p.Key, StringComparer.Ordinal))
        {
            sb.Append(Sep).Append(pair.Key).Append('=');
            AppendValue(sb, pair.Value);
        }

        return sb.ToString();
    }

    private static void AppendValue(StringBuilder sb, object? value)
    {
        switch (value)
        {
            case null:
                sb.Append("null:");
                break;
            case string s:
                sb.Append("str:").Append(s);
                break;
            case bool b:
                sb.Append("bool:").Append(b ? '1' : '0');
                break;
            case IFormattable f:
                sb.Append(value.GetType().Name).Append(':').Append(f.ToString(null, CultureInfo.InvariantCulture));
                break;
            default:
                sb.Append(value.GetType().Name).Append(':').Append(value.ToString());
                break;
        }
    }
}
