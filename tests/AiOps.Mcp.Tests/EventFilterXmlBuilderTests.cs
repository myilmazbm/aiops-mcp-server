using System.Xml;
using AiOps.Mcp.Tools.GetEvents;

namespace AiOps.Mcp.Tests;

public class EventFilterXmlBuilderTests
{
    private static readonly DateTimeOffset Start = new(2026, 7, 1, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset End = new(2026, 7, 1, 12, 30, 0, TimeSpan.Zero);

    private static string SelectText(string filterXml)
    {
        var doc = new XmlDocument();
        doc.LoadXml(filterXml); // also proves the output is well-formed XML
        return doc.SelectSingleNode("/QueryList/Query/Select")!.InnerText;
    }

    [Fact]
    public void Builds_time_window_only_query()
    {
        var xml = EventFilterXmlBuilder.Build("System", null, null, Start, End, null);

        Assert.Equal(
            "*[System[TimeCreated[@SystemTime>='2026-07-01T10:00:00.000Z' and @SystemTime<='2026-07-01T12:30:00.000Z']]]",
            SelectText(xml));
    }

    [Fact]
    public void Combines_event_ids_level_and_time()
    {
        var xml = EventFilterXmlBuilder.Build("System", [7001, 7002], 2, Start, End, null);

        Assert.Equal(
            "*[System[(EventID=7001 or EventID=7002) and Level=2 and " +
            "TimeCreated[@SystemTime>='2026-07-01T10:00:00.000Z' and @SystemTime<='2026-07-01T12:30:00.000Z']]]",
            SelectText(xml));
    }

    [Fact]
    public void Explicit_xpath_overrides_structured_filters()
    {
        var xpath = "*[System[Level=1]] and *[EventData[Data='x < y']]";

        var xml = EventFilterXmlBuilder.Build("Application", [1], 2, Start, End, xpath);

        Assert.Equal(xpath, SelectText(xml));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Blank_xpath_means_not_provided_not_an_empty_query(string xpath)
    {
        // LLM clients routinely send "" for an optional string; an empty <Select> body would
        // make Get-WinEvent reject the query on every server.
        var xml = EventFilterXmlBuilder.Build("System", [7001], 2, Start, End, xpath);

        Assert.Contains("EventID=7001", SelectText(xml));
        Assert.Contains("Level=2", SelectText(xml));
    }

    [Fact]
    public void Channel_name_is_escaped_in_attributes()
    {
        var xml = EventFilterXmlBuilder.Build("Weird\"<Channel>", null, null, Start, End, null);

        var doc = new XmlDocument();
        doc.LoadXml(xml);
        Assert.Equal("Weird\"<Channel>", doc.SelectSingleNode("/QueryList/Query")!.Attributes!["Path"]!.Value);
        Assert.Equal("Weird\"<Channel>", doc.SelectSingleNode("/QueryList/Query/Select")!.Attributes!["Path"]!.Value);
    }

    [Fact]
    public void Converts_local_offsets_to_utc()
    {
        var localStart = new DateTimeOffset(2026, 7, 1, 13, 0, 0, TimeSpan.FromHours(3));
        var localEnd = new DateTimeOffset(2026, 7, 1, 15, 30, 0, TimeSpan.FromHours(3));

        var xml = EventFilterXmlBuilder.Build("System", null, null, localStart, localEnd, null);

        Assert.Contains("@SystemTime>='2026-07-01T10:00:00.000Z'", SelectText(xml));
        Assert.Contains("@SystemTime<='2026-07-01T12:30:00.000Z'", SelectText(xml));
    }
}
