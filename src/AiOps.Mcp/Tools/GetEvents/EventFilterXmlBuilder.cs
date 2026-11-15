using System.Globalization;
using System.Xml.Linq;

namespace AiOps.Mcp.Tools.GetEvents;

/// <summary>
/// The single place where get_events FilterXml is produced. The resulting query is
/// evaluated on the target server by Get-WinEvent -FilterXml, never client-side.
/// </summary>
public static class EventFilterXmlBuilder
{
    public static string Build(
        string channel,
        int[]? eventIds,
        int? level,
        DateTimeOffset start,
        DateTimeOffset end,
        string? xpath)
    {
        // Whitespace-only xpath is "not provided": LLM clients routinely send "" for an
        // optional string, and an empty <Select> body makes Get-WinEvent reject the query.
        var select = string.IsNullOrWhiteSpace(xpath) ? BuildSystemXPath(eventIds, level, start, end) : xpath;

        var query = new XElement("QueryList",
            new XElement("Query",
                new XAttribute("Id", 0),
                new XAttribute("Path", channel),
                new XElement("Select", new XAttribute("Path", channel), select)));

        return query.ToString(SaveOptions.DisableFormatting);
    }

    private static string BuildSystemXPath(int[]? eventIds, int? level, DateTimeOffset start, DateTimeOffset end)
    {
        var conditions = new List<string>();

        if (eventIds is { Length: > 0 })
            conditions.Add("(" + string.Join(" or ", eventIds.Select(id => $"EventID={id}")) + ")");

        if (level is int lvl)
            conditions.Add($"Level={lvl}");

        conditions.Add(
            $"TimeCreated[@SystemTime>='{FormatUtc(start)}' and @SystemTime<='{FormatUtc(end)}']");

        return $"*[System[{string.Join(" and ", conditions)}]]";
    }

    private static string FormatUtc(DateTimeOffset value)
        => value.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
}
