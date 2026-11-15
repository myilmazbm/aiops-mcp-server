using System.Management.Automation;
using AiOps.Mcp.Remoting;
using AiOps.Mcp.Tools.Scom;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using NSubstitute;

namespace AiOps.Mcp.Tests;

public class ScomToolsTests
{
    private const string Ms = "scom01.contoso.local";

    private readonly IRemoteExecutor _executor = Substitute.For<IRemoteExecutor>();
    private RemoteCommand? _sent;
    private string? _sentServer;

    private static IOptions<ScomOptions> Configured => Options.Create(new ScomOptions { ManagementServer = Ms });

    private void Respond(ServerCallResult result)
        => _executor.InvokeAsync(Arg.Do<string>(s => _sentServer = s), Arg.Do<RemoteCommand>(c => _sent = c), Arg.Any<CancellationToken>())
            .Returns(result);

    private static ServerCallResult Ok(params object[] items) => new()
    {
        Server = Ms,
        Status = ServerCallStatus.Ok,
        Output = items.Select(PSObject.AsPSObject).ToList(),
    };

    private sealed class FakeAlert
    {
        public Guid Id { get; init; } = Guid.NewGuid();
        public string Name { get; init; } = "Service stopped";
        public string Description { get; init; } = "The Print Spooler service stopped.";
        public string Severity { get; init; } = "Error";
        public string Priority { get; init; } = "High";
        public byte ResolutionState { get; init; }
        public string MonitoringObjectDisplayName { get; init; } = "SRV01.contoso.local";
        public string MonitoringObjectPath { get; init; } = "SRV01.contoso.local";
        public DateTime TimeRaised { get; init; } = new(2026, 9, 17, 10, 0, 0, DateTimeKind.Utc);
        public DateTime LastModified { get; init; } = new(2026, 9, 17, 10, 5, 0, DateTimeKind.Utc);
        public int RepeatCount { get; init; } = 3;
    }

    private sealed class FakeObject
    {
        public string DisplayName { get; init; } = "SRV01.contoso.local";
        public string FullName { get; init; } = "Microsoft.Windows.Computer:SRV01.contoso.local";
        public string? Path { get; init; }
        public string HealthState { get; init; } = "Success";
        public bool IsAvailable { get; init; } = true;
        public bool InMaintenanceMode { get; init; }
        public DateTime StateLastModified { get; init; } = new(2026, 9, 17, 9, 0, 0, DateTimeKind.Utc);
    }

    // --- get_scom_alerts ---

    [Fact]
    public async Task Alerts_query_targets_the_scom_reader_wrapper_with_server_side_filters()
    {
        Respond(Ok());

        await ScomTools.GetScomAlerts(_executor, Configured, NullLoggerFactory.Instance,
            severity: "warning", resolution_state: 0, monitoring_object: "O'Brien-SRV");

        Assert.Equal(Ms, _sentServer);
        Assert.Equal("AIOpsScomReader", _sent!.Endpoint);
        // The trusted wrapper function does the filtering/sorting/shaping server-side.
        Assert.Equal("Get-AIOpsScomAlerts", _sent.CommandName);
        Assert.Null(_sent.Pipe);
        Assert.Equal("Warning", _sent.Parameters["Severity"]);           // capitalized for the ValidateSet
        Assert.Equal(0, _sent.Parameters["ResolutionState"]);
        Assert.Equal("O'Brien-SRV", _sent.Parameters["ObjectName"]);     // quoting handled inside the wrapper
        // MaxCount is cap+1 so truncation is still detectable.
        Assert.Equal(ScomTools.DefaultMaxAlerts + 1, _sent.Parameters["MaxCount"]);
    }

    [Fact]
    public async Task Null_resolution_state_means_any_state()
    {
        Respond(Ok());

        await ScomTools.GetScomAlerts(_executor, Configured, NullLoggerFactory.Instance, resolution_state: null);

        Assert.False(_sent!.Parameters.ContainsKey("ResolutionState"));
    }

    [Fact]
    public async Task Alerts_are_mapped_truncated_and_ordered_newest_first()
    {
        Respond(Ok(
            new FakeAlert { Name = "older", TimeRaised = new DateTime(2026, 9, 16, 8, 0, 0, DateTimeKind.Utc) },
            new FakeAlert { Name = "newer", Description = new string('d', 5000) }));

        var result = await ScomTools.GetScomAlerts(_executor, Configured, NullLoggerFactory.Instance);

        Assert.Equal("ok", result.Status);
        Assert.Equal(["newer", "older"], result.Alerts.Select(a => a.Name));
        var newest = result.Alerts[0];
        Assert.Equal("Error", newest.Severity);
        Assert.Equal("SRV01.contoso.local", newest.MonitoringObject);
        Assert.Equal(3, newest.RepeatCount);
        Assert.Equal(ScomTools.MaxDescriptionLength, newest.Description!.Length);
        Assert.False(result.Truncated);
    }

    [Fact]
    public async Task Alerts_beyond_the_cap_are_cut_and_flagged()
    {
        Respond(Ok(Enumerable.Range(0, ScomTools.MaxAlertsCeiling + 1).Select(object (i) => new FakeAlert { Name = $"a{i}" }).ToArray()));

        var result = await ScomTools.GetScomAlerts(_executor, Configured, NullLoggerFactory.Instance, max_alerts: 999);

        Assert.Equal(ScomTools.MaxAlertsCeiling, result.Alerts.Count);
        Assert.True(result.Truncated);
    }

    [Fact]
    public async Task Missing_timestamps_do_not_overflow()
    {
        Respond(Ok(new { Name = "no dates" })); // no TimeRaised/LastModified at all

        var result = await ScomTools.GetScomAlerts(_executor, Configured, NullLoggerFactory.Instance);

        Assert.Null(Assert.Single(result.Alerts).TimeRaised);
    }

    [Theory]
    [InlineData("Critical", 0)]
    [InlineData("Error", 999)]
    public async Task Rejects_invalid_severity_or_resolution_state(string severity, int state)
        => await Assert.ThrowsAsync<McpException>(() => ScomTools.GetScomAlerts(
            _executor, Configured, NullLoggerFactory.Instance, severity: severity, resolution_state: state));

    [Fact]
    public async Task Unreachable_management_server_is_reported_not_thrown()
    {
        Respond(new ServerCallResult { Server = Ms, Status = ServerCallStatus.Unreachable, Error = "timeout" });

        var result = await ScomTools.GetScomAlerts(_executor, Configured, NullLoggerFactory.Instance);

        Assert.Equal("unreachable", result.Status);
        Assert.Equal("timeout", result.Error);
    }

    [Fact]
    public async Task Unconfigured_scom_fails_with_guidance_before_any_remote_call()
    {
        var ex = await Assert.ThrowsAsync<McpException>(() => ScomTools.GetScomAlerts(
            _executor, Options.Create(new ScomOptions()), NullLoggerFactory.Instance));

        Assert.Contains("Scom:ManagementServer", ex.Message);
        await _executor.DidNotReceiveWithAnyArgs().InvokeAsync(default!, default!, default);
    }

    // --- get_scom_health ---

    [Fact]
    public async Task Health_query_passes_the_pattern_and_maps_objects()
    {
        Respond(Ok(
            new FakeObject { DisplayName = "SRV02.contoso.local", HealthState = "Error", IsAvailable = false },
            new FakeObject()));

        var result = await ScomTools.GetScomHealth(_executor, Configured, NullLoggerFactory.Instance, "SRV0*");

        Assert.Equal("Get-AIOpsScomHealth", _sent!.CommandName);
        Assert.Equal("SRV0*", _sent.Parameters["Pattern"]);
        Assert.Equal("AIOpsScomReader", _sent.Endpoint);
        Assert.Null(_sent.Pipe);
        Assert.Equal(ScomTools.MaxObjectsCeiling + 1, _sent.Parameters["MaxCount"]);

        Assert.Equal("ok", result.Status);
        Assert.Equal(["SRV01.contoso.local", "SRV02.contoso.local"], result.Objects.Select(o => o.DisplayName)); // sorted
        Assert.Equal("Error", result.Objects[1].HealthState);
        Assert.False(result.Objects[1].IsAvailable);
        Assert.NotNull(result.Objects[0].StateLastModified);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Health_rejects_blank_pattern(string pattern)
        => await Assert.ThrowsAsync<McpException>(() => ScomTools.GetScomHealth(
            _executor, Configured, NullLoggerFactory.Instance, pattern));

    // --- get_scom_performance ---

    private sealed class FakeSeries
    {
        public string Server { get; init; } = "DC01.contoso.local";
        public string MonitoringObject { get; init; } = "DC01.contoso.local";
        public string Counter { get; init; } = @"Processor Information\% Processor Time";
        public string? Instance { get; init; } = "_Total";
        public int SampleCount { get; init; } = 48;
        public double Min { get; init; } = 0.195;
        public double Avg { get; init; } = 0.264;
        public double Max { get; init; } = 0.548;
        public double Last { get; init; } = 0.222;
        public DateTime LastSampledUtc { get; init; } = new(2026, 9, 17, 19, 0, 0, DateTimeKind.Utc);
        public string[]? Samples { get; init; }
    }

    [Fact]
    public async Task Performance_query_passes_window_and_filters_to_the_wrapper()
    {
        Respond(Ok());

        await ScomTools.GetScomPerformance(_executor, Configured, NullLoggerFactory.Instance,
            object_name: "DC01*", counter: "Processor Time", hours: 48, max_series: 10);

        Assert.Equal("Get-AIOpsScomPerformance", _sent!.CommandName);
        Assert.Equal("AIOpsScomReader", _sent.Endpoint);
        Assert.Equal("DC01*", _sent.Parameters["ObjectName"]);
        Assert.Equal("Processor Time", _sent.Parameters["Counter"]);
        Assert.Equal(48, _sent.Parameters["Hours"]);
        Assert.Equal(11, _sent.Parameters["MaxSeries"]); // cap+1 for truncation detection
        Assert.False(_sent.Parameters.ContainsKey("IncludeSamples"));
    }

    [Fact]
    public async Task Performance_clamps_the_window_and_series_cap()
    {
        Respond(Ok());

        await ScomTools.GetScomPerformance(_executor, Configured, NullLoggerFactory.Instance,
            object_name: "*", hours: 99_999, max_series: 99_999);

        Assert.Equal(ScomTools.MaxHours, _sent!.Parameters["Hours"]);
        Assert.Equal(ScomTools.MaxSeriesCeiling + 1, _sent.Parameters["MaxSeries"]);
    }

    [Fact]
    public async Task Performance_maps_aggregates_and_reports_the_window()
    {
        Respond(Ok(new FakeSeries()));

        var result = await ScomTools.GetScomPerformance(_executor, Configured, NullLoggerFactory.Instance, "DC01*", hours: 24);

        Assert.Equal("ok", result.Status);
        var s = Assert.Single(result.Series);
        Assert.Equal("DC01.contoso.local", s.Server);
        Assert.Equal(@"Processor Information\% Processor Time", s.Counter);
        Assert.Equal("_Total", s.Instance);
        Assert.Equal(48, s.SampleCount);
        Assert.Equal(0.264, s.Avg, precision: 10);
        Assert.NotNull(s.LastSampledAt);
        Assert.Null(s.Samples); // raw samples only on request
        // The reported window must match what was asked for.
        Assert.Equal(24, (result.WindowEnd!.Value - result.WindowStart!.Value).TotalHours, precision: 1);
    }

    [Fact]
    public async Task Performance_parses_flat_timestamp_value_samples()
    {
        // The wrapper emits samples as strings because PSRP flattens nested objects at depth 1.
        Respond(Ok(new FakeSeries
        {
            Samples =
            [
                "2026-09-17T18:00:00.0000000Z=1.5",
                "2026-09-17T19:00:00.0000000Z=2.25",
                "malformed-without-separator",
            ],
        }));

        var result = await ScomTools.GetScomPerformance(
            _executor, Configured, NullLoggerFactory.Instance, "DC01*", include_samples: true);

        var samples = Assert.Single(result.Series).Samples;
        Assert.NotNull(samples);
        Assert.Equal(2, samples.Count); // the malformed entry is skipped, not fatal
        Assert.Equal(2.25, samples[1].Value);
        Assert.Equal(new DateTimeOffset(2026, 9, 17, 18, 0, 0, TimeSpan.Zero), samples[0].Timestamp);
        Assert.True(_sent!.Parameters.ContainsKey("IncludeSamples"));
    }

    [Fact]
    public async Task Performance_series_beyond_the_cap_are_cut_and_flagged()
    {
        Respond(Ok(Enumerable.Range(0, 12).Select(object (i) => new FakeSeries { Counter = $@"Obj\Counter{i}" }).ToArray()));

        var result = await ScomTools.GetScomPerformance(
            _executor, Configured, NullLoggerFactory.Instance, "DC01*", max_series: 10);

        Assert.Equal(10, result.Series.Count);
        Assert.True(result.Truncated);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Performance_rejects_blank_object_name(string pattern)
        => await Assert.ThrowsAsync<McpException>(() => ScomTools.GetScomPerformance(
            _executor, Configured, NullLoggerFactory.Instance, pattern));

    // --- get_scom_inventory ---

    private sealed class FakeComputer
    {
        public string Computer { get; init; } = "DC01.contoso.local";
        public string HealthState { get; init; } = "Success";
        public bool InMaintenanceMode { get; init; }
        public string[] Properties { get; init; } =
        [
            "DNSName=DC01.contoso.local",
            "IPAddress=10.0.0.10, fe80::1",           // value legitimately contains commas
            "OrganizationalUnit=OU=Domain Controllers,DC=contoso,DC=local", // and '=' signs
            "IsVirtualMachine=True",
            "OS.BuildNumber=26100",
            "AD.PDCEmulator=DC01.contoso.local",
            "malformed-no-separator",
        ];
        public string[] Disks { get; init; } = ["C:|NTFS|64427|Success", "|||"];
        public string[] NetworkAdapters { get; init; } =
            ["Ethernet|00-15-5D-01-6A-2B|10.0.0.10|255.255.255.0|10.0.0.1|False"];
    }

    [Fact]
    public async Task Inventory_maps_the_flattened_property_bag()
    {
        Respond(Ok(new FakeComputer()));

        var result = await ScomTools.GetScomInventory(_executor, Configured, NullLoggerFactory.Instance, "DC01*");

        Assert.Equal("Get-AIOpsScomInventory", _sent!.CommandName);
        Assert.Equal("AIOpsScomReader", _sent.Endpoint);
        Assert.Equal("ok", result.Status);
        var c = Assert.Single(result.Computers);
        Assert.Equal("Success", c.HealthState);
        Assert.Equal("DC01.contoso.local", c.Properties["DNSName"]);
        Assert.Equal("10.0.0.10, fe80::1", c.Properties["IPAddress"]);
        // Split on the FIRST '=' only: a DN value keeps its own '=' signs intact.
        Assert.Equal("OU=Domain Controllers,DC=contoso,DC=local", c.Properties["OrganizationalUnit"]);
        Assert.False(c.Properties.ContainsKey("malformed-no-separator"));
        // OS and directory-role properties share the bag under their prefixes.
        Assert.Equal("26100", c.Properties["OS.BuildNumber"]);
        Assert.Equal("DC01.contoso.local", c.Properties["AD.PDCEmulator"]);
    }

    [Fact]
    public async Task Inventory_parses_disks_and_network_adapters()
    {
        Respond(Ok(new FakeComputer()));

        var result = await ScomTools.GetScomInventory(_executor, Configured, NullLoggerFactory.Instance, "DC01*");

        var c = Assert.Single(result.Computers);
        var disk = Assert.Single(c.Disks); // the all-empty row is skipped, not mapped
        Assert.Equal("C:", disk.Name);
        Assert.Equal("NTFS", disk.FileSystem);
        Assert.Equal(64427, disk.SizeMb);
        Assert.Equal("Success", disk.HealthState);

        var nic = Assert.Single(c.NetworkAdapters);
        Assert.Equal("Ethernet", nic.Name);
        Assert.Equal("00-15-5D-01-6A-2B", nic.MacAddress);
        Assert.Equal("10.0.0.1", nic.Gateway);
        Assert.False(nic.DhcpEnabled);
    }

    [Fact]
    public async Task Inventory_tolerates_short_or_missing_child_rows()
    {
        // A wrapper that emits fewer fields (older role capability) must not crash the tool.
        Respond(Ok(new FakeComputer { Disks = ["D:|NTFS"], NetworkAdapters = [] }));

        var result = await ScomTools.GetScomInventory(_executor, Configured, NullLoggerFactory.Instance, "DC01*");

        var disk = Assert.Single(Assert.Single(result.Computers).Disks);
        Assert.Equal("D:", disk.Name);
        Assert.Null(disk.SizeMb);
        Assert.Empty(result.Computers[0].NetworkAdapters);
    }

    [Fact]
    public async Task Inventory_beyond_the_cap_is_cut_and_flagged()
    {
        Respond(Ok(Enumerable.Range(0, 7).Select(object (i) => new FakeComputer { Computer = $"SRV{i:D2}" }).ToArray()));

        var result = await ScomTools.GetScomInventory(
            _executor, Configured, NullLoggerFactory.Instance, "*", max_computers: 5);

        Assert.Equal(5, result.Computers.Count);
        Assert.True(result.Truncated);
        Assert.Equal(6, _sent!.Parameters["MaxComputers"]); // cap+1
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Inventory_rejects_blank_computer_name(string pattern)
        => await Assert.ThrowsAsync<McpException>(() => ScomTools.GetScomInventory(
            _executor, Configured, NullLoggerFactory.Instance, pattern));

    // --- get_scom_alert_detail ---

    private const string AlertGuid = "edf59354-bd37-4a89-b4f2-1e1ce42cd626";

    private sealed class FakeAlertDetail
    {
        public string Id { get; init; } = AlertGuid;
        public string Name { get; init; } = "MSSQL on Windows: Product version compliance";
        public string Description { get; init; } = "The product version number is outdated.";
        public string Severity { get; init; } = "Warning";
        public int ResolutionState { get; init; }
        public string MonitoringObjectDisplayName { get; init; } = @"SQL01\MSSQLSERVER";
        public string MonitoringObjectHealthState { get; init; } = "Warning";
        public DateTime TimeRaised { get; init; } = new(2026, 9, 16, 7, 38, 44, DateTimeKind.Utc);
        public string SourceKind { get; init; } = "Monitor";
        public string SourceName { get; init; } = "Product Version Compliance";
        public string TargetClass { get; init; } = "MSSQL on Windows: DB Engine";
        public string ManagementPack { get; init; } = "Microsoft.SQLServer.Windows.Monitoring";
        public string ManagementPackVersion { get; init; } = "7.10.6.0";
        public string KnowledgeArticle { get; init; } = "This monitor checks the product version number.";
        public string[] Overrides { get; init; } =
            ["Threshold|30000|Microsoft.SystemCenter.ManagementServer|System Center Core Monitoring|False"];
        public string[] History { get; init; } =
        [
            "2026-09-16T07:38:44.0000000Z|System|0|Alert Activated by the System.",
            "2026-09-17T13:27:55.0000000Z|System|255|Alert Resolved by the System.",
        ];
    }

    [Fact]
    public async Task Alert_detail_maps_the_root_cause_chain()
    {
        Respond(Ok(new FakeAlertDetail()));

        var result = await ScomTools.GetScomAlertDetail(_executor, Configured, NullLoggerFactory.Instance, AlertGuid);

        Assert.Equal("Get-AIOpsScomAlertDetail", _sent!.CommandName);
        Assert.Equal(AlertGuid, _sent.Parameters["AlertId"]);
        Assert.Equal("AIOpsScomReader", _sent.Endpoint);

        Assert.Equal("ok", result.Status);
        var a = result.Alert!;
        Assert.Equal("Monitor", a.SourceKind);
        Assert.Equal("Product Version Compliance", a.SourceName);
        Assert.Equal("MSSQL on Windows: DB Engine", a.TargetClass);
        Assert.Equal("7.10.6.0", a.ManagementPackVersion);
        Assert.Contains("product version", a.KnowledgeArticle!, StringComparison.OrdinalIgnoreCase);

        var ovr = Assert.Single(a.Overrides);
        Assert.Equal("Threshold", ovr.Field);      // configuration overrides carry Parameter, property ones carry Property
        Assert.Equal("30000", ovr.Value);
        Assert.False(ovr.Enforced);

        Assert.Equal(2, a.History.Count);
        Assert.Equal(255, a.History[1].ResolutionState);
        Assert.Equal("System", a.History[0].ModifiedBy);
    }

    [Fact]
    public async Task Alert_detail_truncates_a_long_knowledge_article()
    {
        Respond(Ok(new FakeAlertDetail { KnowledgeArticle = new string('k', 9000) }));

        var result = await ScomTools.GetScomAlertDetail(_executor, Configured, NullLoggerFactory.Instance, AlertGuid);

        Assert.Equal(ScomTools.MaxKnowledgeLength, result.Alert!.KnowledgeArticle!.Length);
    }

    [Fact]
    public async Task Alert_detail_reports_notfound_rather_than_failing()
    {
        // A groomed-away alert is a normal answer, not an error.
        Respond(Ok());

        var result = await ScomTools.GetScomAlertDetail(_executor, Configured, NullLoggerFactory.Instance, AlertGuid);

        Assert.Equal("notfound", result.Status);
        Assert.Null(result.Alert);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-guid")]
    public async Task Alert_detail_rejects_a_non_guid_id(string id)
        => await Assert.ThrowsAsync<McpException>(() => ScomTools.GetScomAlertDetail(
            _executor, Configured, NullLoggerFactory.Instance, id));

    // --- get_scom_state_changes ---

    private sealed class FakeStateChange
    {
        public string Server { get; init; } = "EXC01.contoso.local";
        public string MonitoringObject { get; init; } = "EXC01.contoso.local";
        public string Monitor { get; init; } = "Health Service Heartbeat Failure";
        public DateTime TimeGeneratedUtc { get; init; } = new(2026, 9, 16, 6, 47, 3, DateTimeKind.Utc);
        public string OldHealthState { get; init; } = "Success";
        public string NewHealthState { get; init; } = "Error";
    }

    [Fact]
    public async Task State_changes_are_returned_newest_first_within_the_window()
    {
        Respond(Ok(
            new FakeStateChange { TimeGeneratedUtc = new DateTime(2026, 9, 16, 6, 47, 3, DateTimeKind.Utc) },
            new FakeStateChange
            {
                TimeGeneratedUtc = new DateTime(2026, 9, 16, 6, 52, 33, DateTimeKind.Utc),
                OldHealthState = "Error",
                NewHealthState = "Success",
            }));

        var result = await ScomTools.GetScomStateChanges(
            _executor, Configured, NullLoggerFactory.Instance, "EXC01*", hours: 48);

        Assert.Equal("Get-AIOpsScomStateChanges", _sent!.CommandName);
        Assert.Equal(48, _sent.Parameters["Hours"]);
        Assert.Equal(ScomTools.DefaultMaxChanges + 1, _sent.Parameters["MaxChanges"]);
        Assert.False(_sent.Parameters.ContainsKey("OnlyUnhealthy"));

        Assert.Equal("ok", result.Status);
        Assert.Equal(2, result.Changes.Count);
        Assert.Equal("Success", result.Changes[0].ToState);   // 06:52 recovery first
        Assert.Equal("Error", result.Changes[1].ToState);     // 06:47 failure second
        Assert.Equal("Health Service Heartbeat Failure", result.Changes[0].Monitor);
        Assert.Equal(48, (result.WindowEnd!.Value - result.WindowStart!.Value).TotalHours, precision: 1);
    }

    [Fact]
    public async Task State_changes_pass_the_only_unhealthy_switch_through()
    {
        Respond(Ok());

        await ScomTools.GetScomStateChanges(
            _executor, Configured, NullLoggerFactory.Instance, "DC01*", only_unhealthy: true);

        Assert.True((bool)_sent!.Parameters["OnlyUnhealthy"]!);
    }

    [Fact]
    public async Task State_changes_beyond_the_cap_are_cut_and_flagged()
    {
        Respond(Ok(Enumerable.Range(0, 7).Select(object (i) => new FakeStateChange
        {
            TimeGeneratedUtc = new DateTime(2026, 9, 16, 6, i, 0, DateTimeKind.Utc),
        }).ToArray()));

        var result = await ScomTools.GetScomStateChanges(
            _executor, Configured, NullLoggerFactory.Instance, "*", max_changes: 5);

        Assert.Equal(5, result.Changes.Count);
        Assert.True(result.Truncated);
    }

    // --- get_scom_monitoring_health ---

    private sealed class FakeMonitoringHealth
    {
        public string ManagementGroup { get; init; } = "SCOM-MG";
        public string ManagementGroupVersion { get; init; } = "10.25.10132.0";
        public bool IsConnected { get; init; } = true;
        public int AgentCount { get; init; } = 6;
        public int GreyAgentCount { get; init; } = 1;
        public int PendingAgentCount { get; init; }
        public int NotificationSubscriptionCount { get; init; }
        public int HeartbeatIntervalSeconds { get; init; } = 60;
        public int MissingHeartbeatThreshold { get; init; } = 3;
        public int PerformanceRetentionDays { get; init; } = 7;
        public int StateChangeRetentionDays { get; init; } = 3;
        public string[] ManagementServers { get; init; } = ["SCOM01.contoso.local|Success|10.25.10132.0|True"];
        public string[] Agents { get; init; } = ["DC01.contoso.local|Success|10.25.10079.0|SCOM01.contoso.local|True"];
        public string[] GreyAgents { get; init; } = ["EXC01.contoso.local|Error|False|2026-09-16T06:47:03.0000000Z"];
        public string[] InMaintenanceMode { get; init; } =
            ["WEB01.contoso.local|2026-09-17T20:00:00.0000000Z|2026-09-17T22:00:00.0000000Z|PlannedOther|CONTOSO\\dev"];
        public string[] ComputersWithoutAgent { get; init; } = ["SCOM01.contoso.local"];
    }

    [Fact]
    public async Task Monitoring_health_maps_the_whole_report()
    {
        Respond(Ok(new FakeMonitoringHealth()));

        var result = await ScomTools.GetScomMonitoringHealth(_executor, Configured, NullLoggerFactory.Instance);

        Assert.Equal("Get-AIOpsScomMonitoringHealth", _sent!.CommandName);
        Assert.Equal("ok", result.Status);
        Assert.Equal("SCOM-MG", result.ManagementGroup);
        Assert.True(result.IsConnected);
        Assert.Equal(6, result.AgentCount);
        Assert.Equal(7, result.PerformanceRetentionDays);   // bounds how far back any history query can go
        Assert.Equal(3, result.StateChangeRetentionDays);
        Assert.Equal(0, result.NotificationSubscriptionCount);

        Assert.True(Assert.Single(result.ManagementServers).IsRootManagementServer);
        Assert.Equal("SCOM01.contoso.local", result.Agents[0].PrimaryManagementServer);

        var grey = Assert.Single(result.GreyAgents);
        Assert.Equal("EXC01.contoso.local", grey.Name);
        Assert.False(grey.IsAvailable);
        Assert.NotNull(grey.StateLastModified);

        var mm = Assert.Single(result.InMaintenanceMode);
        Assert.Equal("WEB01.contoso.local", mm.Object);
        Assert.Equal("PlannedOther", mm.Reason);
        Assert.NotNull(mm.ScheduledEnd);

        Assert.Equal(["SCOM01.contoso.local"], result.ComputersWithoutAgent);
    }

    [Fact]
    public async Task Monitoring_health_reports_an_empty_response_as_error()
    {
        Respond(Ok());

        var result = await ScomTools.GetScomMonitoringHealth(_executor, Configured, NullLoggerFactory.Instance);

        Assert.Equal("error", result.Status);
        Assert.Contains("No monitoring health data", result.Error);
    }

    [Fact]
    public async Task Monitoring_health_surfaces_an_unreachable_management_server()
    {
        Respond(new ServerCallResult { Server = Ms, Status = ServerCallStatus.Unreachable, Error = "timeout" });

        var result = await ScomTools.GetScomMonitoringHealth(_executor, Configured, NullLoggerFactory.Instance);

        Assert.Equal("unreachable", result.Status);
    }

    // --- timestamp frame ---
    // Every SCOM time crosses the PSRP boundary either as a typed DateTime or as a string the
    // wrapper flattened. Measured on the live management server, the typed ones arrive with
    // Kind=Unspecified even though SCOM stores UTC. The old code let the implicit
    // DateTime->DateTimeOffset conversion stamp the MCP host's local offset, which put every
    // SCOM timestamp in a different frame from the UTC window the wrapper filtered on. These
    // tests pin the frame; the rest of the suite hard-codes Kind=Utc and cannot see it.

    [Fact]
    public async Task Unspecified_kind_timestamps_are_read_as_utc_not_host_local()
    {
        Respond(Ok(new FakeAlert { TimeRaised = new DateTime(2026, 9, 17, 13, 14, 25) })); // Unspecified

        var result = await ScomTools.GetScomAlerts(_executor, Configured, NullLoggerFactory.Instance);

        var raised = Assert.Single(result.Alerts).TimeRaised;
        Assert.Equal(TimeSpan.Zero, raised!.Value.Offset);
        Assert.Equal(new DateTimeOffset(2026, 9, 17, 13, 14, 25, TimeSpan.Zero), raised.Value);
    }

    [Fact]
    public async Task Local_kind_timestamps_keep_their_instant_rather_than_being_relabelled()
    {
        // Get-WinEvent yields Kind=Local and PSRP preserves the instant, so Local must be
        // converted to UTC, never stamped as if it were already UTC.
        var local = new DateTime(2026, 9, 17, 13, 14, 25, DateTimeKind.Local);
        Respond(Ok(new FakeAlert { TimeRaised = local }));

        var result = await ScomTools.GetScomAlerts(_executor, Configured, NullLoggerFactory.Instance);

        var raised = Assert.Single(result.Alerts).TimeRaised;
        Assert.Equal(TimeSpan.Zero, raised!.Value.Offset);
        Assert.Equal(local.ToUniversalTime(), raised.Value.UtcDateTime);
    }

    [Fact]
    public async Task State_change_timestamps_land_inside_the_reported_window()
    {
        // The window is built in C# from UtcNow while the wrapper filters on [datetime]::UtcNow.
        // If the returned times carried the host offset instead, a recent change would be
        // stamped hours before WindowStart and read as stale data.
        var justNow = DateTime.UtcNow.AddMinutes(-5);
        Respond(Ok(new FakeStateChange
        {
            TimeGeneratedUtc = new DateTime(justNow.Ticks, DateTimeKind.Unspecified),
        }));

        var result = await ScomTools.GetScomStateChanges(
            _executor, Configured, NullLoggerFactory.Instance, "EXC01*", hours: 1);

        var change = Assert.Single(result.Changes);
        Assert.InRange(change.At!.Value, result.WindowStart!.Value, result.WindowEnd!.Value);
    }

    [Fact]
    public async Task Flattened_timestamp_without_an_offset_marker_is_read_as_utc()
    {
        // The wrapper now emits round-trip UTC, but a value that reaches us without a marker
        // must still be read as UTC rather than as local time on the MCP host.
        Respond(Ok(new FakeMonitoringHealth
        {
            InMaintenanceMode = ["WEB01.contoso.local|2026-09-17T20:00:00|2026-09-17T22:00:00|PlannedOther|dev"],
        }));

        var result = await ScomTools.GetScomMonitoringHealth(_executor, Configured, NullLoggerFactory.Instance);

        var window = Assert.Single(result.InMaintenanceMode);
        Assert.Equal(new DateTimeOffset(2026, 9, 17, 20, 0, 0, TimeSpan.Zero), window.Start);
        Assert.Equal(new DateTimeOffset(2026, 9, 17, 22, 0, 0, TimeSpan.Zero), window.ScheduledEnd);
    }
}
