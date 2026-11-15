using AiOps.Contracts;
using AiOps.Mcp.Remoting;
using AiOps.Mcp.Tools.Scom;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AiOps.Mcp.LabTests;

/// <summary>
/// Integration tests against the lab SCOM management server with the AIOpsScomReader JEA
/// endpoint registered. Set AIOPS_SCOM_SERVER to its FQDN to enable; skipped otherwise.
/// </summary>
public class ScomLabTests
{
    private static string? ScomServer => Environment.GetEnvironmentVariable("AIOPS_SCOM_SERVER");

    private static (PsSessionPool Pool, JeaRemoteExecutor Executor, IOptions<ScomOptions> Scom) Create()
    {
        var remoting = Options.Create(new RemotingOptions());
        var pool = new PsSessionPool(new WsmanRunspaceConnector(remoting), remoting, NullLogger<PsSessionPool>.Instance);
        var executor = new JeaRemoteExecutor(pool, remoting, NullLogger<JeaRemoteExecutor>.Instance);
        return (pool, executor, Options.Create(new ScomOptions { ManagementServer = ScomServer }));
    }

    [SkippableFact]
    public async Task Reads_open_alerts_through_the_scom_reader_endpoint()
    {
        Skip.If(string.IsNullOrEmpty(ScomServer), "AIOPS_SCOM_SERVER not set");
        var (pool, executor, scom) = Create();
        using var _ = pool;

        var result = await ScomTools.GetScomAlerts(executor, scom, NullLoggerFactory.Instance, resolution_state: null, max_alerts: 20);

        Assert.Equal("ok", result.Status);
        Assert.All(result.Alerts, a => Assert.False(string.IsNullOrEmpty(a.Name)));
        Assert.All(result.Alerts, a => Assert.True(a.Description is null || a.Description.Length <= ScomTools.MaxDescriptionLength));
    }

    [SkippableFact]
    public async Task Reads_health_of_monitored_computers()
    {
        Skip.If(string.IsNullOrEmpty(ScomServer), "AIOPS_SCOM_SERVER not set");
        var (pool, executor, scom) = Create();
        using var _ = pool;

        var result = await ScomTools.GetScomHealth(executor, scom, NullLoggerFactory.Instance, "*");

        Assert.Equal("ok", result.Status);
        Assert.NotEmpty(result.Objects);
        Assert.All(result.Objects, o => Assert.Contains(o.HealthState, new[] { "Success", "Warning", "Error", "Uninitialized" }));
    }

    // --- get_scom_performance ---

    [SkippableFact]
    public async Task Reads_historical_performance_for_a_monitored_host()
    {
        Skip.If(string.IsNullOrEmpty(ScomServer), "AIOPS_SCOM_SERVER not set");
        var (pool, executor, scom) = Create();
        using var _ = pool;

        var result = await ScomTools.GetScomPerformance(executor, scom, NullLoggerFactory.Instance,
            object_name: "*", counter: "Processor Time", hours: 24, max_series: 20);

        Assert.Equal("ok", result.Status);
        // Summary statistics must be internally consistent, and raw samples withheld by default.
        Assert.All(result.Series, s =>
        {
            Assert.False(string.IsNullOrEmpty(s.Counter));
            Assert.True(s.SampleCount > 0);
            Assert.True(s.Min <= s.Avg && s.Avg <= s.Max);
            Assert.True(s.Samples is null || s.Samples.Count == 0);
        });
    }

    [SkippableFact]
    public async Task Raw_performance_samples_are_returned_only_on_request()
    {
        Skip.If(string.IsNullOrEmpty(ScomServer), "AIOPS_SCOM_SERVER not set");
        var (pool, executor, scom) = Create();
        using var _ = pool;

        var result = await ScomTools.GetScomPerformance(executor, scom, NullLoggerFactory.Instance,
            object_name: "*", counter: "Processor Time", hours: 24, max_series: 5, include_samples: true);

        Assert.Equal("ok", result.Status);
        Skip.If(result.Series.Count == 0, "no performance data collected in the lab window");
        Assert.Contains(result.Series, s => s.Samples is { Count: > 0 });
    }

    // --- get_scom_inventory ---

    [SkippableFact]
    public async Task Reads_discovered_inventory_including_hosts_the_fleet_endpoint_cannot_reach()
    {
        Skip.If(string.IsNullOrEmpty(ScomServer), "AIOPS_SCOM_SERVER not set");
        var (pool, executor, scom) = Create();
        using var _ = pool;

        var result = await ScomTools.GetScomInventory(executor, scom, NullLoggerFactory.Instance,
            computer_name: "*", max_computers: 50);

        Assert.Equal("ok", result.Status);
        Assert.NotEmpty(result.Computers);
        Assert.All(result.Computers, c => Assert.False(string.IsNullOrEmpty(c.Computer)));
        // The point of this tool: hosts without a JEA endpoint still get inventoried.
        Assert.Contains(result.Computers, c => c.Properties.ContainsKey("IPAddress") || c.NetworkAdapters.Count > 0);
    }

    // --- get_scom_alert_detail ---

    [SkippableFact]
    public async Task Alert_detail_resolves_the_workflow_that_raised_an_alert()
    {
        Skip.If(string.IsNullOrEmpty(ScomServer), "AIOPS_SCOM_SERVER not set");
        var (pool, executor, scom) = Create();
        using var _ = pool;

        var alerts = await ScomTools.GetScomAlerts(executor, scom, NullLoggerFactory.Instance,
            resolution_state: null, max_alerts: 1);
        Skip.If(alerts.Alerts.Count == 0, "no alerts in the lab management group");

        var result = await ScomTools.GetScomAlertDetail(executor, scom, NullLoggerFactory.Instance,
            alerts.Alerts[0].Id);

        Assert.Equal("ok", result.Status);
        var detail = Assert.IsType<ScomAlertDetailDto>(result.Alert);
        Assert.Equal(alerts.Alerts[0].Id, detail.Id);
        Assert.Contains(detail.SourceKind, new string?[] { "Monitor", "Rule", null });
        Assert.True(detail.KnowledgeArticle is null || detail.KnowledgeArticle.Length <= ScomTools.MaxKnowledgeLength);
    }

    [SkippableFact]
    public async Task Unknown_alert_id_is_reported_as_notfound_not_thrown()
    {
        Skip.If(string.IsNullOrEmpty(ScomServer), "AIOPS_SCOM_SERVER not set");
        var (pool, executor, scom) = Create();
        using var _ = pool;

        var result = await ScomTools.GetScomAlertDetail(executor, scom, NullLoggerFactory.Instance,
            Guid.Empty.ToString());

        Assert.Equal("notfound", result.Status);
        Assert.Null(result.Alert);
    }

    // --- get_scom_state_changes ---

    [SkippableFact]
    public async Task Reads_monitor_state_transitions_within_the_window()
    {
        Skip.If(string.IsNullOrEmpty(ScomServer), "AIOPS_SCOM_SERVER not set");
        var (pool, executor, scom) = Create();
        using var _ = pool;

        var result = await ScomTools.GetScomStateChanges(executor, scom, NullLoggerFactory.Instance,
            object_name: "*", hours: 168, max_changes: 50);

        Assert.Equal("ok", result.Status);
        Assert.NotNull(result.WindowStart);
        Assert.All(result.Changes, c =>
        {
            Assert.False(string.IsNullOrEmpty(c.Monitor));
            Assert.InRange(c.At!.Value, result.WindowStart!.Value.AddMinutes(-5), DateTimeOffset.UtcNow.AddMinutes(5));
        });
    }

    [SkippableFact]
    public async Task Only_unhealthy_filters_out_transitions_back_to_success()
    {
        Skip.If(string.IsNullOrEmpty(ScomServer), "AIOPS_SCOM_SERVER not set");
        var (pool, executor, scom) = Create();
        using var _ = pool;

        var result = await ScomTools.GetScomStateChanges(executor, scom, NullLoggerFactory.Instance,
            object_name: "*", hours: 168, only_unhealthy: true, max_changes: 50);

        Assert.Equal("ok", result.Status);
        Assert.All(result.Changes, c => Assert.NotEqual("Success", c.ToState));
    }

    // --- get_scom_monitoring_health ---

    [SkippableFact]
    public async Task Monitoring_health_reports_the_management_group_and_its_agents()
    {
        Skip.If(string.IsNullOrEmpty(ScomServer), "AIOPS_SCOM_SERVER not set");
        var (pool, executor, scom) = Create();
        using var _ = pool;

        var result = await ScomTools.GetScomMonitoringHealth(executor, scom, NullLoggerFactory.Instance);

        Assert.Equal("ok", result.Status);
        Assert.False(string.IsNullOrEmpty(result.ManagementGroup));
        Assert.True(result.IsConnected);
        Assert.NotEmpty(result.ManagementServers);
        Assert.Equal(result.AgentCount, result.Agents.Count);
        // Grey agents are a strict subset of the agent list, not a separate population.
        Assert.Equal(result.GreyAgentCount, result.GreyAgents.Count);
        Assert.All(result.GreyAgents, g => Assert.Contains(result.Agents, a => a.Name == g.Name));
        // Retention bounds every history query; zero would mean the grooming read failed.
        Assert.True(result.PerformanceRetentionDays > 0);
        Assert.True(result.StateChangeRetentionDays > 0);
        Assert.True(result.HeartbeatIntervalSeconds > 0);
    }
}
