using System.Globalization;
using System.Text;
using System.Text.Json;
using AiOps.Contracts;

namespace AiOps.Mcp.Alerts;

/// <summary>
/// Maps the loosely-shaped JSON that SCOM command channels emit onto <see cref="ScomAlert"/>.
/// Field names vary between channel scripts, so common aliases are accepted; anything
/// unrecognized is ignored. Every free-text field is sanitized and capped at ingestion —
/// the webhook is unauthenticated, so no field may reach logs/forwarder unbounded or with
/// control characters (log-line forgery).
/// </summary>
public static class ScomAlertNormalizer
{
    public const int MaxIdLength = 128;
    public const int MaxNameLength = 300;
    public const int MaxDescriptionLength = 2000;
    public const int MaxServerLength = 300;
    public const int MaxPriorityLength = 64;
    public const int MaxSeverityLength = 64;

    /// <summary>
    /// Explicit formats tried in order. The fleet is Turkish-locale, so a channel script's
    /// short date is 'dd.MM.yyyy'; InvariantCulture parsing would read that as MM.dd and
    /// silently shift the alert time by months. ISO 8601 is tried first for well-behaved
    /// channels; the round-trip DateTimeOffset.TryParse fallback catches offset-bearing forms.
    /// </summary>
    /// <summary>ISO 8601 forms: offset-bearing or, when offset-less, treated as UTC.</summary>
    private static readonly string[] IsoFormats =
    [
        "yyyy-MM-ddTHH:mm:ss.FFFFFFFK",
        "yyyy-MM-ddTHH:mm:ssK",
        "yyyy-MM-dd HH:mm:ss",
        "yyyy-MM-dd",
    ];

    /// <summary>
    /// Locale short-date forms a channel script emits from a server's local clock. These are
    /// interpreted as LOCAL time of this host (same fleet, same time zone), not UTC —
    /// assuming UTC would shift every alert by the zone offset.
    /// </summary>
    private static readonly string[] LocalFormats =
    [
        "dd.MM.yyyy HH:mm:ss",
        "dd.MM.yyyy HH:mm",
        "dd.MM.yyyy",
        "MM/dd/yyyy HH:mm:ss",
        "MM/dd/yyyy",
    ];

    private static readonly string[] IdAliases = ["alertId", "id"];
    private static readonly string[] NameAliases = ["alertName", "name", "title"];
    private static readonly string[] DescriptionAliases = ["description"];
    private static readonly string[] SeverityAliases = ["severity"];
    private static readonly string[] PriorityAliases = ["priority"];
    private static readonly string[] ServerAliases =
        ["monitoringObjectDisplayName", "computer", "server", "monitoringObjectPath", "netbiosComputerName"];
    private static readonly string[] TimeAliases = ["timeRaised", "timeAdded", "createdOn"];

    /// <summary>Returns null (with a reason) when the payload has no usable alert name.</summary>
    public static (ScomAlert? Alert, string? Error) Normalize(JsonElement payload, DateTimeOffset receivedAt)
    {
        if (payload.ValueKind != JsonValueKind.Object)
            return (null, "Payload must be a JSON object.");

        var name = Clean(GetString(payload, NameAliases), MaxNameLength);
        if (name is null)
            return (null, "Alert has no name (expected one of: alertName, name, title).");

        var alert = new ScomAlert
        {
            AlertId = Clean(GetString(payload, IdAliases), MaxIdLength) ?? Guid.NewGuid().ToString(),
            Name = name,
            Description = Clean(GetString(payload, DescriptionAliases), MaxDescriptionLength),
            Severity = Clean(NormalizeSeverity(GetString(payload, SeverityAliases)), MaxSeverityLength),
            Priority = Clean(GetString(payload, PriorityAliases), MaxPriorityLength),
            Server = Clean(GetString(payload, ServerAliases), MaxServerLength),
            TimeRaised = ParseTime(GetString(payload, TimeAliases)),
            ReceivedAt = receivedAt,
        };

        return (alert, null);
    }

    private static string? GetString(JsonElement payload, string[] aliases)
    {
        // Alias order is precedence: the more specific SCOM field must win even when a
        // generic one appears earlier in the payload.
        foreach (var alias in aliases)
        {
            foreach (var property in payload.EnumerateObject())
            {
                if (!string.Equals(property.Name, alias, StringComparison.OrdinalIgnoreCase))
                    continue;

                var value = property.Value.ValueKind switch
                {
                    JsonValueKind.String => property.Value.GetString(),
                    JsonValueKind.Number => property.Value.GetRawText(),
                    _ => null,
                };

                if (!string.IsNullOrWhiteSpace(value))
                    return value;
            }
        }

        return null;
    }

    /// <summary>
    /// SCOM severities arrive as 0/1/2 or as names; normalize to the SDK's own vocabulary,
    /// which is Information / Warning / Error. The Operations Console labels severity 2
    /// "Critical", and taking that label here split the repo in two: an alert forwarded from
    /// the webhook said "Critical", while get_scom_alerts accepts and returns only
    /// Error/Warning/Information — so feeding a forwarded alert's severity straight back into
    /// the tool was rejected. "Error" is what Get-SCOMAlert emits, so that is what wins.
    /// </summary>
    internal static string? NormalizeSeverity(string? raw)
        => raw?.Trim() switch
        {
            null or "" => null,
            "0" => "Information",
            "1" => "Warning",
            "2" => "Error",
            var s when s.Equals("information", StringComparison.OrdinalIgnoreCase) => "Information",
            var s when s.Equals("warning", StringComparison.OrdinalIgnoreCase) => "Warning",
            var s when s.Equals("critical", StringComparison.OrdinalIgnoreCase) => "Error",
            var s when s.Equals("error", StringComparison.OrdinalIgnoreCase) => "Error",
            var s => s,
        };

    internal static DateTimeOffset? ParseTime(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        raw = raw.Trim();
        if (DateTimeOffset.TryParseExact(raw, IsoFormats, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AllowWhiteSpaces, out var iso))
            return iso;

        if (DateTimeOffset.TryParseExact(raw, LocalFormats, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeLocal | DateTimeStyles.AllowWhiteSpaces, out var local))
            return local;

        // Round-trip / offset-bearing forms not covered by the explicit list.
        return DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.RoundtripKind, out var value)
            ? value
            : null;
    }

    /// <summary>Trim, strip control characters (CRLF → log-line forgery), then length-cap.</summary>
    internal static string? Clean(string? value, int max)
    {
        if (value is null)
            return null;

        var sb = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            // Keep printable text; collapse any control character (incl. CR/LF/TAB) to a space.
            sb.Append(char.IsControl(ch) ? ' ' : ch);
        }

        var cleaned = sb.ToString().Trim();
        return cleaned.Length == 0 ? null : TextUtil.Truncate(cleaned, max);
    }
}
