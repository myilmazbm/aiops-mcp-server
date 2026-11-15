using System.Text.Json;
using AiOps.Mcp.Alerts;

namespace AiOps.Mcp.Tests;

public class ScomAlertNormalizerTests
{
    private static readonly DateTimeOffset Received = new(2026, 7, 6, 12, 0, 0, TimeSpan.Zero);

    private static (AiOps.Contracts.ScomAlert? Alert, string? Error) Normalize(string json)
    {
        using var document = JsonDocument.Parse(json);
        return ScomAlertNormalizer.Normalize(document.RootElement.Clone(), Received);
    }

    [Fact]
    public void Maps_a_typical_scom_command_channel_payload()
    {
        var (alert, error) = Normalize("""
            {
              "AlertId": "guid-123",
              "AlertName": "Service stopped",
              "Description": "The Print Spooler service stopped unexpectedly.",
              "Severity": 2,
              "Priority": "High",
              "MonitoringObjectDisplayName": "SRV1.contoso.local",
              "TimeRaised": "2026-07-06T09:30:00Z"
            }
            """);

        Assert.Null(error);
        Assert.Equal("guid-123", alert!.AlertId);
        Assert.Equal("Service stopped", alert.Name);
        Assert.Equal("Error", alert.Severity);
        Assert.Equal("High", alert.Priority);
        Assert.Equal("SRV1.contoso.local", alert.Server);
        Assert.Equal(new DateTimeOffset(2026, 7, 6, 9, 30, 0, TimeSpan.Zero), alert.TimeRaised);
        Assert.Equal(Received, alert.ReceivedAt);
    }

    [Fact]
    public void Specific_aliases_win_over_generic_ones_regardless_of_payload_order()
    {
        var (alert, _) = Normalize("""{"name":"generic","alertName":"specific"}""");

        Assert.Equal("specific", alert!.Name);
    }

    [Fact]
    public void Missing_alert_id_gets_a_generated_one()
    {
        var (alert, _) = Normalize("""{"name":"x"}""");

        Assert.True(Guid.TryParse(alert!.AlertId, out _));
    }

    [Fact]
    public void Payload_without_a_name_is_rejected_with_guidance()
    {
        var (alert, error) = Normalize("""{"severity":2,"computer":"SRV1"}""");

        Assert.Null(alert);
        Assert.Contains("alertName", error);
    }

    [Fact]
    public void Non_object_payload_is_rejected()
    {
        var (alert, error) = Normalize("""["not","an","object"]""");

        Assert.Null(alert);
        Assert.Contains("JSON object", error);
    }

    [Theory]
    [InlineData("0", "Information")]
    [InlineData("1", "Warning")]
    [InlineData("2", "Error")]
    [InlineData("critical", "Error")]  // console label -> SDK vocabulary
    [InlineData("WARNING", "Warning")]
    [InlineData("MatchMonitorHealth", "MatchMonitorHealth")] // unknown values pass through
    [InlineData(null, null)]
    [InlineData("  ", null)]
    public void Severity_normalizes_numbers_and_names(string? raw, string? expected)
        => Assert.Equal(expected, ScomAlertNormalizer.NormalizeSeverity(raw));

    [Fact]
    public void Numeric_severity_in_json_is_handled()
    {
        var (alert, _) = Normalize("""{"name":"x","severity":1}""");

        Assert.Equal("Warning", alert!.Severity);
    }

    [Fact]
    public void Free_text_fields_are_truncated_at_ingestion()
    {
        var json = JsonSerializer.Serialize(new
        {
            name = new string('n', 5000),
            description = new string('d', 50_000),
            computer = new string('c', 5000),
        });

        var (alert, _) = Normalize(json);

        Assert.Equal(ScomAlertNormalizer.MaxNameLength, alert!.Name.Length);
        Assert.Equal(ScomAlertNormalizer.MaxDescriptionLength, alert.Description!.Length);
        Assert.Equal(ScomAlertNormalizer.MaxServerLength, alert.Server!.Length);
    }

    [Theory]
    [InlineData("not a date")]
    [InlineData("")]
    public void Unparseable_time_raised_becomes_null(string raw)
    {
        var (alert, _) = Normalize($$"""{"name":"x","timeRaised":"{{raw}}"}""");

        Assert.Null(alert!.TimeRaised);
    }

    [Theory]
    [InlineData("2026-07-03T14:05:22Z", 2026, 7, 3, 14, 5, 22)]
    [InlineData("2026-07-03 14:05:22", 2026, 7, 3, 14, 5, 22)]
    public void Iso_time_raised_is_utc(string raw, int y, int mo, int d, int h, int mi, int s)
    {
        var (alert, _) = Normalize($$"""{"name":"x","timeRaised":"{{raw}}"}""");

        Assert.Equal(new DateTimeOffset(y, mo, d, h, mi, s, TimeSpan.Zero), alert!.TimeRaised!.Value.ToUniversalTime());
    }

    [Theory]
    // Turkish short-date 'dd.MM.yyyy' must not be read as MM.dd (InvariantCulture bug), and a
    // channel script's local wall-clock time must be interpreted as LOCAL, not UTC —
    // assuming UTC would shift every alert by the zone offset.
    [InlineData("03.07.2026 14:05:22", 2026, 7, 3, 14, 5, 22)]
    [InlineData("25.07.2026 09:00:00", 2026, 7, 25, 9, 0, 0)] // day>12: previously dropped to null
    [InlineData("03.07.2026", 2026, 7, 3, 0, 0, 0)]
    public void Locale_short_date_is_local_wall_clock_time(string raw, int y, int mo, int d, int h, int mi, int s)
    {
        var (alert, _) = Normalize($$"""{"name":"x","timeRaised":"{{raw}}"}""");

        var expectedLocal = new DateTime(y, mo, d, h, mi, s, DateTimeKind.Local);
        var parsed = alert!.TimeRaised!.Value;
        Assert.Equal(expectedLocal, parsed.DateTime);
        Assert.Equal(TimeZoneInfo.Local.GetUtcOffset(expectedLocal), parsed.Offset);
    }

    [Fact]
    public void Offset_bearing_timestamps_survive_the_fallback_parse()
    {
        var (alert, _) = Normalize("""{"name":"x","timeRaised":"2026-07-03T17:05:22+03:00"}""");

        Assert.Equal(new DateTimeOffset(2026, 7, 3, 14, 5, 22, TimeSpan.Zero), alert!.TimeRaised!.Value.ToUniversalTime());
    }

    [Fact]
    public void All_free_text_fields_including_id_priority_severity_are_capped()
    {
        var json = JsonSerializer.Serialize(new
        {
            name = "x",
            alertId = new string('i', 10_000),
            priority = new string('p', 10_000),
            severity = new string('s', 10_000), // unrecognized severity falls through
        });

        var (alert, _) = Normalize(json);

        Assert.Equal(ScomAlertNormalizer.MaxIdLength, alert!.AlertId.Length);
        Assert.Equal(ScomAlertNormalizer.MaxPriorityLength, alert.Priority!.Length);
        Assert.Equal(ScomAlertNormalizer.MaxSeverityLength, alert.Severity!.Length);
    }

    [Fact]
    public void Control_characters_are_stripped_to_prevent_log_line_forgery()
    {
        var (alert, _) = Normalize("""{"name":"Disk full\r\n2026 [ERR] injected admin login","description":"a\tb\nc"}""");

        Assert.DoesNotContain('\r', alert!.Name);
        Assert.DoesNotContain('\n', alert.Name);
        Assert.DoesNotContain('\n', alert.Description!);
        Assert.DoesNotContain('\t', alert.Description!);
    }

    [Fact]
    public void Unknown_fields_are_ignored_not_fatal()
    {
        var (alert, error) = Normalize("""{"name":"x","customField":{"nested":true},"list":[1,2]}""");

        Assert.Null(error);
        Assert.Equal("x", alert!.Name);
    }

    [Fact]
    public void A_forwarded_alerts_severity_is_accepted_by_get_scom_alerts()
    {
        // The two halves of the repo used different vocabularies: the webhook emitted the
        // console label "Critical" while the tools accept only the SDK names. An LLM reading a
        // forwarded alert and querying for more of the same was rejected with an McpException.
        foreach (var raw in new[] { "0", "1", "2", "information", "warning", "critical", "error" })
        {
            var normalized = ScomAlertNormalizer.NormalizeSeverity(raw);
            Assert.Contains(normalized, new[] { "Information", "Warning", "Error" });
        }
    }
}
