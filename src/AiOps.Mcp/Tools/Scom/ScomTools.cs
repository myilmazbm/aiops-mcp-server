using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Management.Automation;
using AiOps.Contracts;
using AiOps.Mcp.Remoting;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace AiOps.Mcp.Tools.Scom;

/// <summary>
/// Reads from SCOM through the SDK cmdlets exposed by the AIOpsScomReader JEA endpoint on
/// the management server. Everything SCOM already monitors (including domain controllers,
/// which are out of scope for direct JEA access) is reachable through here, read-only.
/// </summary>
[McpServerToolType]
public static class ScomTools
{
    public const int DefaultMaxAlerts = 100;
    /// <summary>Hard cap; requests above it are clamped, never honored.</summary>
    public const int MaxAlertsCeiling = 200;
    public const int MaxObjectsCeiling = 100;
    public const int MaxDescriptionLength = 1000;
    public const int DefaultHours = 24;
    /// <summary>30 days; SCOM's operational database grooms well before this.</summary>
    public const int MaxHours = 720;
    public const int DefaultMaxSeries = 100;
    public const int MaxSeriesCeiling = 200;
    public const int DefaultMaxComputers = 50;
    public const int MaxComputersCeiling = 100;
    public const int DefaultMaxChanges = 100;
    public const int MaxChangesCeiling = 200;
    /// <summary>Knowledge articles are vendor prose; the LLM needs the gist, not the page.</summary>
    public const int MaxKnowledgeLength = 1500;
    public const int MonitoringHealthMaxRows = 200;

    private static readonly HashSet<string> Severities = new(StringComparer.OrdinalIgnoreCase) { "Error", "Warning", "Information" };

    [McpServerTool(Name = "get_scom_alerts", ReadOnly = true)]
    [Description("Lists SCOM alerts from the management server. Filter by severity (Error|Warning|Information), resolution state and a substring of the monitored object's display name (e.g. a server name). resolution_state defaults to 0, which is New only: a tuned management group moves acknowledged-but-still-open alerts to custom states (249 Acknowledged, 247 Assigned, and others between 1 and 254), and those are NOT returned by the default. Pass resolution_state=null to see every alert including acknowledged ones before concluding a server has no open alerts; 255 is Closed. Descriptions are truncated to 1000 characters.")]
    public static async Task<GetScomAlertsResult> GetScomAlerts(
        IRemoteExecutor executor,
        IOptions<ScomOptions> options,
        ILoggerFactory loggerFactory,
        [Description("Error | Warning | Information (optional)")] string? severity = null,
        [Description("0 = New only (default; excludes acknowledged states 1-254), 255 = Closed, null = any state")] int? resolution_state = 0,
        [Description("Substring of the monitored object's display name, e.g. 'SRV01' (optional)")] string? monitoring_object = null,
        [Description("Maximum alerts to return (default 100, hard cap 200)")] int max_alerts = DefaultMaxAlerts,
        CancellationToken cancellationToken = default)
    {
        var scom = RequireConfigured(options);
        if (severity is not null && !Severities.Contains(severity))
            throw new McpException("severity must be one of Error, Warning, Information.");
        if (resolution_state is < 0 or > 255)
            throw new McpException("resolution_state must be between 0 and 255.");

        var maxAlerts = Math.Clamp(max_alerts, 1, MaxAlertsCeiling);

        // Filtering, sorting and shaping run inside the trusted Get-AIOpsScomAlerts wrapper on
        // the management server (see AIOpsScomReader.psrc). MaxCount is maxAlerts+1 so the tool
        // can still detect truncation.
        var parameters = new List<(string, object?)> { ("MaxCount", maxAlerts + 1) };
        if (severity is not null)
            parameters.Add(("Severity", Capitalize(severity)));
        if (resolution_state is int state)
            parameters.Add(("ResolutionState", state));
        if (!string.IsNullOrWhiteSpace(monitoring_object))
            parameters.Add(("ObjectName", monitoring_object.Trim()));

        var command = RemoteCommand.Create("Get-AIOpsScomAlerts", parameters.ToArray())
            .OnEndpoint(scom.JeaEndpointName);

        var stopwatch = Stopwatch.StartNew();
        var call = await executor.InvokeAsync(scom.ManagementServer!, command, cancellationToken).ConfigureAwait(false);

        var alerts = call.Output
            .Select(MapAlert)
            .Where(a => a is not null)
            .Select(a => a!)
            .OrderByDescending(a => a.TimeRaised ?? DateTimeOffset.MinValue)
            .ToList();
        var truncated = alerts.Count > maxAlerts;
        if (truncated)
            alerts = alerts.Take(maxAlerts).ToList();

        loggerFactory.CreateLogger("AiOps.Mcp.Tools.get_scom_alerts").LogInformation(
            "get_scom_alerts severity={Severity} state={State} object={Object} status={Status} alerts={Count} took {ElapsedMs} ms",
            severity, resolution_state, monitoring_object, call.Status, alerts.Count, stopwatch.ElapsedMilliseconds);

        return new GetScomAlertsResult
        {
            Status = call.Status.ToWire(),
            Error = call.Error,
            Truncated = truncated,
            Alerts = alerts,
        };
    }

    [McpServerTool(Name = "get_scom_health", ReadOnly = true)]
    [Description("Returns the SCOM health state of monitored objects whose display name matches the pattern (wildcards allowed, e.g. 'SRV01*' or '*SQL*'). Use it for anything SCOM monitors, including domain controllers.")]
    public static async Task<GetScomHealthResult> GetScomHealth(
        IRemoteExecutor executor,
        IOptions<ScomOptions> options,
        ILoggerFactory loggerFactory,
        [Description("Display-name pattern; * and ? wildcards allowed")] string object_name,
        [Description("Maximum objects to return (default 100, hard cap 100)")] int max_objects = MaxObjectsCeiling,
        CancellationToken cancellationToken = default)
    {
        var scom = RequireConfigured(options);
        if (string.IsNullOrWhiteSpace(object_name))
            throw new McpException("object_name is required.");

        var maxObjects = Math.Clamp(max_objects, 1, MaxObjectsCeiling);

        var command = RemoteCommand.Create(
                "Get-AIOpsScomHealth", ("Pattern", object_name.Trim()), ("MaxCount", maxObjects + 1))
            .OnEndpoint(scom.JeaEndpointName);

        var stopwatch = Stopwatch.StartNew();
        var call = await executor.InvokeAsync(scom.ManagementServer!, command, cancellationToken).ConfigureAwait(false);

        var objects = call.Output
            .Select(MapObject)
            .Where(o => o is not null)
            .Select(o => o!)
            .OrderBy(o => o.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var truncated = objects.Count > maxObjects;
        if (truncated)
            objects = objects.Take(maxObjects).ToList();

        loggerFactory.CreateLogger("AiOps.Mcp.Tools.get_scom_health").LogInformation(
            "get_scom_health pattern={Pattern} status={Status} objects={Count} took {ElapsedMs} ms",
            object_name, call.Status, objects.Count, stopwatch.ElapsedMilliseconds);

        return new GetScomHealthResult
        {
            Status = call.Status.ToWire(),
            Error = call.Error,
            Truncated = truncated,
            Objects = objects,
        };
    }

    [McpServerTool(Name = "get_scom_performance", ReadOnly = true)]
    [Description("Historical performance counters SCOM collected for a host, aggregated (min/avg/max/last) over a time window. Complements get_counters: that one samples live over WinRM, this one gives trends over hours/days AND covers hosts the fleet JEA endpoint cannot reach, such as domain controllers (including AD-specific counters: replication queue, RID pool, ntds.dit size, LDAP sessions, FSMO latencies).")]
    public static async Task<GetScomPerformanceResult> GetScomPerformance(
        IRemoteExecutor executor,
        IOptions<ScomOptions> options,
        ILoggerFactory loggerFactory,
        [Description("Host or monitored-object name pattern, e.g. 'DC01.contoso.local' or 'SQL*'; wildcards allowed")] string object_name,
        [Description("Optional substring of the counter, e.g. 'Processor Time', 'Free Space', 'AD Replication'")] string? counter = null,
        [Description("How many hours back to look (default 24, max 720)")] int hours = DefaultHours,
        [Description("Maximum counter series to return (default 100, hard cap 200)")] int max_series = DefaultMaxSeries,
        [Description("Return raw samples in addition to the summary statistics")] bool include_samples = false,
        CancellationToken cancellationToken = default)
    {
        var scom = RequireConfigured(options);
        if (string.IsNullOrWhiteSpace(object_name))
            throw new McpException("object_name is required.");

        var window = Math.Clamp(hours, 1, MaxHours);
        var maxSeries = Math.Clamp(max_series, 1, MaxSeriesCeiling);

        var parameters = new List<(string, object?)>
        {
            ("ObjectName", object_name.Trim()),
            ("Hours", window),
            ("MaxSeries", maxSeries + 1),
        };
        if (!string.IsNullOrWhiteSpace(counter))
            parameters.Add(("Counter", counter.Trim()));
        if (include_samples)
            parameters.Add(("IncludeSamples", true));

        var command = RemoteCommand.Create("Get-AIOpsScomPerformance", parameters.ToArray())
            .OnEndpoint(scom.JeaEndpointName);

        var stopwatch = Stopwatch.StartNew();
        var call = await executor.InvokeAsync(scom.ManagementServer!, command, cancellationToken).ConfigureAwait(false);

        var series = call.Output.Select(MapSeries).Where(s => s is not null).Select(s => s!).ToList();
        var truncated = series.Count > maxSeries;
        if (truncated)
            series = series.Take(maxSeries).ToList();

        loggerFactory.CreateLogger("AiOps.Mcp.Tools.get_scom_performance").LogInformation(
            "get_scom_performance object={Object} counter={Counter} hours={Hours} status={Status} series={Count} took {ElapsedMs} ms",
            object_name, counter, window, call.Status, series.Count, stopwatch.ElapsedMilliseconds);

        var end = DateTimeOffset.UtcNow;
        return new GetScomPerformanceResult
        {
            Status = call.Status.ToWire(),
            Error = call.Error,
            Truncated = truncated,
            WindowStart = end.AddHours(-window),
            WindowEnd = end,
            Series = series,
        };
    }

    [McpServerTool(Name = "get_scom_inventory", ReadOnly = true)]
    [Description("What SCOM discovered about a computer without logging into it: DNS name, IP addresses, domain, organizational unit, forest, virtual-machine flag, OS details. This is the inventory source for hosts the fleet JEA endpoint cannot reach, such as domain controllers (get_inventory cannot read those).")]
    public static async Task<GetScomInventoryResult> GetScomInventory(
        IRemoteExecutor executor,
        IOptions<ScomOptions> options,
        ILoggerFactory loggerFactory,
        [Description("Computer name pattern, e.g. 'DC01.contoso.local' or '*' for all monitored computers")] string computer_name,
        [Description("Maximum computers to return (default 50, hard cap 100)")] int max_computers = DefaultMaxComputers,
        CancellationToken cancellationToken = default)
    {
        var scom = RequireConfigured(options);
        if (string.IsNullOrWhiteSpace(computer_name))
            throw new McpException("computer_name is required.");

        var maxComputers = Math.Clamp(max_computers, 1, MaxComputersCeiling);

        var command = RemoteCommand.Create(
                "Get-AIOpsScomInventory",
                ("ComputerName", computer_name.Trim()),
                ("MaxComputers", maxComputers + 1))
            .OnEndpoint(scom.JeaEndpointName);

        var stopwatch = Stopwatch.StartNew();
        var call = await executor.InvokeAsync(scom.ManagementServer!, command, cancellationToken).ConfigureAwait(false);

        var computers = call.Output.Select(MapComputer).Where(c => c is not null).Select(c => c!)
            .OrderBy(c => c.Computer, StringComparer.OrdinalIgnoreCase).ToList();
        var truncated = computers.Count > maxComputers;
        if (truncated)
            computers = computers.Take(maxComputers).ToList();

        loggerFactory.CreateLogger("AiOps.Mcp.Tools.get_scom_inventory").LogInformation(
            "get_scom_inventory pattern={Pattern} status={Status} computers={Count} took {ElapsedMs} ms",
            computer_name, call.Status, computers.Count, stopwatch.ElapsedMilliseconds);

        return new GetScomInventoryResult
        {
            Status = call.Status.ToWire(),
            Error = call.Error,
            Truncated = truncated,
            Computers = computers,
        };
    }

    [McpServerTool(Name = "get_scom_alert_detail", ReadOnly = true)]
    [Description("Root-cause chain for one SCOM alert: the monitor or rule that raised it, the class it targets, the management pack that owns it, the vendor's knowledge article (cause and resolution text), the overrides in effect (the usual reason a threshold differs or nothing alerted), and the resolution timeline. Call this after get_scom_alerts with an alert id.")]
    public static async Task<GetScomAlertDetailResult> GetScomAlertDetail(
        IRemoteExecutor executor,
        IOptions<ScomOptions> options,
        ILoggerFactory loggerFactory,
        [Description("Alert id (GUID) from get_scom_alerts")] string alert_id,
        CancellationToken cancellationToken = default)
    {
        var scom = RequireConfigured(options);
        if (string.IsNullOrWhiteSpace(alert_id))
            throw new McpException("alert_id is required.");
        if (!Guid.TryParse(alert_id.Trim(), out _))
            throw new McpException("alert_id must be a GUID as returned by get_scom_alerts.");

        var command = RemoteCommand.Create("Get-AIOpsScomAlertDetail", ("AlertId", alert_id.Trim()))
            .OnEndpoint(scom.JeaEndpointName);

        var stopwatch = Stopwatch.StartNew();
        var call = await executor.InvokeAsync(scom.ManagementServer!, command, cancellationToken).ConfigureAwait(false);
        var detail = call.Output.Select(MapAlertDetail).FirstOrDefault(d => d is not null);

        loggerFactory.CreateLogger("AiOps.Mcp.Tools.get_scom_alert_detail").LogInformation(
            "get_scom_alert_detail alert={AlertId} status={Status} found={Found} took {ElapsedMs} ms",
            alert_id, call.Status, detail is not null, stopwatch.ElapsedMilliseconds);

        // An unknown id is not an error: the alert may simply have been groomed away.
        var status = call.Status != ServerCallStatus.Ok ? call.Status.ToWire()
            : detail is null ? "notfound" : "ok";

        return new GetScomAlertDetailResult { Status = status, Error = call.Error, Alert = detail };
    }

    [McpServerTool(Name = "get_scom_state_changes", ReadOnly = true)]
    [Description("Timeline of SCOM monitor health transitions for a host: which monitor flipped, when, and from which state to which. Answers 'what changed and when' — SCOM's own record, which live WinRM queries cannot reconstruct. Use only_unhealthy to see just the degradations.")]
    public static async Task<GetScomStateChangesResult> GetScomStateChanges(
        IRemoteExecutor executor,
        IOptions<ScomOptions> options,
        ILoggerFactory loggerFactory,
        [Description("Host or monitored-object name pattern, e.g. 'DC01.contoso.local' or 'SQL*'")] string object_name,
        [Description("How many hours back to look (default 24, max 720)")] int hours = DefaultHours,
        [Description("Return only transitions INTO a non-healthy state")] bool only_unhealthy = false,
        [Description("Maximum transitions to return (default 100, hard cap 200)")] int max_changes = DefaultMaxChanges,
        CancellationToken cancellationToken = default)
    {
        var scom = RequireConfigured(options);
        if (string.IsNullOrWhiteSpace(object_name))
            throw new McpException("object_name is required.");

        var window = Math.Clamp(hours, 1, MaxHours);
        var maxChanges = Math.Clamp(max_changes, 1, MaxChangesCeiling);

        var parameters = new List<(string, object?)>
        {
            ("ObjectName", object_name.Trim()),
            ("Hours", window),
            ("MaxChanges", maxChanges + 1),
        };
        if (only_unhealthy)
            parameters.Add(("OnlyUnhealthy", true));

        var command = RemoteCommand.Create("Get-AIOpsScomStateChanges", parameters.ToArray())
            .OnEndpoint(scom.JeaEndpointName);

        var stopwatch = Stopwatch.StartNew();
        var call = await executor.InvokeAsync(scom.ManagementServer!, command, cancellationToken).ConfigureAwait(false);

        var changes = call.Output.Select(MapStateChange).Where(c => c is not null).Select(c => c!)
            .OrderByDescending(c => c.At ?? DateTimeOffset.MinValue).ToList();
        var truncated = changes.Count > maxChanges;
        if (truncated)
            changes = changes.Take(maxChanges).ToList();

        loggerFactory.CreateLogger("AiOps.Mcp.Tools.get_scom_state_changes").LogInformation(
            "get_scom_state_changes object={Object} hours={Hours} status={Status} changes={Count} took {ElapsedMs} ms",
            object_name, window, call.Status, changes.Count, stopwatch.ElapsedMilliseconds);

        var end = DateTimeOffset.UtcNow;
        return new GetScomStateChangesResult
        {
            Status = call.Status.ToWire(),
            Error = call.Error,
            Truncated = truncated,
            WindowStart = end.AddHours(-window),
            WindowEnd = end,
            Changes = changes,
        };
    }

    [McpServerTool(Name = "get_scom_monitoring_health", ReadOnly = true)]
    [Description("Whether the monitoring system itself is healthy and complete: grey agents (hosts SCOM has stopped hearing from), heartbeat settings, objects in maintenance mode, management-server health, pending agents, how far back data is retained, and discovered computers with no agent. Check this before trusting — or doubting — any other SCOM answer.")]
    public static async Task<GetScomMonitoringHealthResult> GetScomMonitoringHealth(
        IRemoteExecutor executor,
        IOptions<ScomOptions> options,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken = default)
    {
        var scom = RequireConfigured(options);

        var command = RemoteCommand.Create("Get-AIOpsScomMonitoringHealth", ("MaxRows", MonitoringHealthMaxRows + 1))
            .OnEndpoint(scom.JeaEndpointName);

        var stopwatch = Stopwatch.StartNew();
        var call = await executor.InvokeAsync(scom.ManagementServer!, command, cancellationToken).ConfigureAwait(false);
        var row = call.Output.FirstOrDefault();

        loggerFactory.CreateLogger("AiOps.Mcp.Tools.get_scom_monitoring_health").LogInformation(
            "get_scom_monitoring_health status={Status} took {ElapsedMs} ms", call.Status, stopwatch.ElapsedMilliseconds);

        if (row is null)
        {
            return new GetScomMonitoringHealthResult
            {
                Status = call.Status == ServerCallStatus.Ok ? "error" : call.Status.ToWire(),
                Error = call.Error ?? "No monitoring health data returned",
            };
        }

        // The wrapper is asked for cap+1 rows per list so truncation is detectable, the same
        // way every other SCOM tool does it. Without this, a 500-server fleet always got the
        // first 200 agents with nothing to say the list was partial, and the LLM read a
        // 200-entry ComputersWithoutAgent list as the complete coverage gap.
        var lists = new[] { "ManagementServers", "Agents", "GreyAgents", "InMaintenanceMode", "ComputersWithoutAgent" };
        var truncated = lists.Any(name => PsValue.GetMany(row, name).Count() > MonitoringHealthMaxRows);

        // A read the SCOM role refused is reported, never rendered as a zero: "0 agents,
        // retention 0 days, every computer without an agent" with status ok is exactly what
        // this tool returned for a Read-Only Operator before the wrapper named its refusals.
        var unavailable = PsValue.GetMany(row, "Unavailable")
            .Select(o => o.BaseObject as string).Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!).ToList();
        var status = call.Status.ToWire();
        var error = call.Error;
        if (unavailable.Count > 0 && call.Status == ServerCallStatus.Ok)
        {
            status = "partial";
            error = "Not readable with the service account's SCOM role (needs SCOM Administrator): " +
                    string.Join(", ", unavailable) +
                    ". The affected settings are null and agents/management servers carry name and health only.";
        }

        return new GetScomMonitoringHealthResult
        {
            Status = status,
            Error = error,
            Unavailable = unavailable,
            Truncated = truncated,
            ManagementGroup = PsValue.Get<string>(row, "ManagementGroup"),
            ManagementGroupVersion = PsValue.Get<string>(row, "ManagementGroupVersion"),
            IsConnected = PsValue.Get<bool>(row, "IsConnected"),
            AgentCount = PsValue.Get<int>(row, "AgentCount"),
            GreyAgentCount = PsValue.Get<int>(row, "GreyAgentCount"),
            PendingAgentCount = PsValue.Get<int?>(row, "PendingAgentCount"),
            NotificationSubscriptionCount = PsValue.Get<int?>(row, "NotificationSubscriptionCount"),
            HeartbeatIntervalSeconds = PsValue.Get<int?>(row, "HeartbeatIntervalSeconds"),
            MissingHeartbeatThreshold = PsValue.Get<int?>(row, "MissingHeartbeatThreshold"),
            PerformanceRetentionDays = PsValue.Get<int?>(row, "PerformanceRetentionDays"),
            StateChangeRetentionDays = PsValue.Get<int?>(row, "StateChangeRetentionDays"),
            ManagementServers = ParseDelimited(row, "ManagementServers", f => new ScomManagementServerDto
            {
                Name = f[0],
                HealthState = Blank(f.ElementAtOrDefault(1)),
                Version = Blank(f.ElementAtOrDefault(2)),
                IsRootManagementServer = bool.TryParse(f.ElementAtOrDefault(3), out var rms) && rms,
            }, MonitoringHealthMaxRows),
            Agents = ParseDelimited(row, "Agents", f => new ScomAgentDto
            {
                Name = f[0],
                HealthState = Blank(f.ElementAtOrDefault(1)),
                Version = Blank(f.ElementAtOrDefault(2)),
                PrimaryManagementServer = Blank(f.ElementAtOrDefault(3)),
                ProxyingEnabled = bool.TryParse(f.ElementAtOrDefault(4), out var proxy) ? proxy : null,
            }, MonitoringHealthMaxRows),
            GreyAgents = ParseDelimited(row, "GreyAgents", f => new ScomAgentDto
            {
                Name = f[0],
                HealthState = Blank(f.ElementAtOrDefault(1)),
                IsAvailable = bool.TryParse(f.ElementAtOrDefault(2), out var avail) ? avail : null,
                StateLastModified = ParseOffset(f.ElementAtOrDefault(3)),
            }, MonitoringHealthMaxRows),
            InMaintenanceMode = ParseDelimited(row, "InMaintenanceMode", f => new ScomMaintenanceDto
            {
                Object = f[0],
                Start = ParseOffset(f.ElementAtOrDefault(1)),
                ScheduledEnd = ParseOffset(f.ElementAtOrDefault(2)),
                Reason = Blank(f.ElementAtOrDefault(3)),
                User = Blank(f.ElementAtOrDefault(4)),
            }, MonitoringHealthMaxRows),
            ComputersWithoutAgent = PsValue.GetMany(row, "ComputersWithoutAgent")
                .Select(o => o.BaseObject as string).Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => s!).Take(MonitoringHealthMaxRows).ToList(),
        };
    }

    internal static ScomAlertDetailDto? MapAlertDetail(PSObject obj)
    {
        var id = PsValue.Get<string>(obj, "Id");
        var name = PsValue.Get<string>(obj, "Name");
        if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(name))
            return null;

        var description = PsValue.Get<string>(obj, "Description");
        var knowledge = PsValue.Get<string>(obj, "KnowledgeArticle");

        return new ScomAlertDetailDto
        {
            Id = id,
            Name = name,
            Description = description is null ? null : TextUtil.Truncate(description, MaxDescriptionLength),
            Severity = PsValue.Get<string>(obj, "Severity"),
            ResolutionState = PsValue.Get<int>(obj, "ResolutionState"),
            MonitoringObject = PsValue.Get<string>(obj, "MonitoringObjectDisplayName"),
            MonitoringObjectHealthState = Blank(PsValue.Get<string>(obj, "MonitoringObjectHealthState")),
            TimeRaised = ToOffset(PsValue.Get<DateTime>(obj, "TimeRaised")),
            SourceKind = Blank(PsValue.Get<string>(obj, "SourceKind")),
            SourceName = Blank(PsValue.Get<string>(obj, "SourceName")),
            TargetClass = Blank(PsValue.Get<string>(obj, "TargetClass")),
            ManagementPack = Blank(PsValue.Get<string>(obj, "ManagementPack")),
            ManagementPackVersion = Blank(PsValue.Get<string>(obj, "ManagementPackVersion")),
            // Vendor articles run long; the LLM needs the gist, not the whole page.
            KnowledgeArticle = string.IsNullOrWhiteSpace(knowledge) ? null : TextUtil.Truncate(knowledge, MaxKnowledgeLength),
            Overrides = ParseDelimited(obj, "Overrides", f => new ScomOverrideDto
            {
                Field = f[0],
                Value = Blank(f.ElementAtOrDefault(1)),
                Target = Blank(f.ElementAtOrDefault(2)),
                ManagementPack = Blank(f.ElementAtOrDefault(3)),
                Enforced = bool.TryParse(f.ElementAtOrDefault(4), out var enforced) && enforced,
            }),
            History = ParseDelimited(obj, "History", f => new ScomAlertHistoryDto
            {
                At = ParseOffset(f[0]),
                ModifiedBy = Blank(f.ElementAtOrDefault(1)),
                ResolutionState = int.TryParse(f.ElementAtOrDefault(2), NumberStyles.Integer, CultureInfo.InvariantCulture, out var rs) ? rs : 0,
                Comment = Blank(f.ElementAtOrDefault(3)),
            }),
        };
    }

    internal static ScomStateChangeDto? MapStateChange(PSObject obj)
    {
        var monitor = PsValue.Get<string>(obj, "Monitor");
        if (string.IsNullOrEmpty(monitor))
            return null;

        return new ScomStateChangeDto
        {
            Server = PsValue.Get<string>(obj, "Server") ?? "",
            MonitoringObject = Blank(PsValue.Get<string>(obj, "MonitoringObject")),
            Monitor = monitor,
            At = ToOffset(PsValue.Get<DateTime>(obj, "TimeGeneratedUtc")),
            FromState = Blank(PsValue.Get<string>(obj, "OldHealthState")),
            ToState = Blank(PsValue.Get<string>(obj, "NewHealthState")),
        };
    }

    /// <summary>
    /// Parses a timestamp the wrapper flattened into a string. AssumeUniversal is the point:
    /// a value that carries no offset marker is UTC (everything SCOM emits is), not a local
    /// time on the MCP host. AdjustToUniversal then normalizes an explicit offset to UTC so
    /// every timestamp this file returns is in the same frame as the UTC window the wrapper
    /// filtered on ([datetime]::UtcNow in the .psrc).
    /// </summary>
    private static DateTimeOffset? ParseOffset(string? value)
        => DateTimeOffset.TryParse(
            value, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var at)
            ? at
            : null;

    internal static ScomPerformanceSeriesDto? MapSeries(PSObject obj)
    {
        var counter = PsValue.Get<string>(obj, "Counter");
        if (string.IsNullOrEmpty(counter))
            return null;

        return new ScomPerformanceSeriesDto
        {
            Server = PsValue.Get<string>(obj, "Server") ?? "",
            MonitoringObject = PsValue.Get<string>(obj, "MonitoringObject"),
            Counter = counter,
            Instance = PsValue.Get<string>(obj, "Instance"),
            SampleCount = PsValue.Get<int>(obj, "SampleCount"),
            Min = PsValue.Get<double>(obj, "Min"),
            Avg = PsValue.Get<double>(obj, "Avg"),
            Max = PsValue.Get<double>(obj, "Max"),
            Last = PsValue.Get<double>(obj, "Last"),
            LastSampledAt = ToOffset(PsValue.Get<DateTime>(obj, "LastSampledUtc")),
            Samples = ParseSamples(obj),
        };
    }

    /// <summary>
    /// Samples arrive as "o-format-timestamp=value" strings: a flat string array survives
    /// PSRP's depth-1 serialization, where an array of objects would not.
    /// </summary>
    private static List<CounterSampleDto>? ParseSamples(PSObject obj)
    {
        var raw = PsValue.GetMany(obj, "Samples").Select(s => s.BaseObject as string).Where(s => s is not null).ToList();
        if (raw.Count == 0)
            return null;

        var samples = new List<CounterSampleDto>(raw.Count);
        foreach (var entry in raw)
        {
            var split = entry!.LastIndexOf('=');
            if (split <= 0)
                continue;
            if (!DateTimeOffset.TryParse(entry[..split], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at))
                continue;
            if (!double.TryParse(entry[(split + 1)..], NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                continue;
            samples.Add(new CounterSampleDto { Timestamp = at, Value = value });
        }

        return samples.Count == 0 ? null : samples;
    }

    internal static ScomComputerDto? MapComputer(PSObject obj)
    {
        var computer = PsValue.Get<string>(obj, "Computer");
        if (string.IsNullOrEmpty(computer))
            return null;

        // Same depth-1 reason as samples: the wrapper flattens the property bag to "Name=Value".
        var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in PsValue.GetMany(obj, "Properties"))
        {
            if (entry.BaseObject is not string text)
                continue;
            var split = text.IndexOf('=');
            if (split <= 0)
                continue;
            properties[text[..split]] = text[(split + 1)..];
        }

        return new ScomComputerDto
        {
            Computer = computer,
            HealthState = PsValue.Get<string>(obj, "HealthState"),
            InMaintenanceMode = PsValue.Get<bool>(obj, "InMaintenanceMode"),
            Properties = properties,
            Disks = ParseDelimited(obj, "Disks", f => new ScomDiskDto
            {
                Name = f[0],
                FileSystem = Blank(f.ElementAtOrDefault(1)),
                SizeMb = double.TryParse(f.ElementAtOrDefault(2), NumberStyles.Float, CultureInfo.InvariantCulture, out var mb) ? mb : null,
                HealthState = Blank(f.ElementAtOrDefault(3)),
            }),
            NetworkAdapters = ParseDelimited(obj, "NetworkAdapters", f => new ScomNetworkAdapterDto
            {
                Name = f[0],
                MacAddress = Blank(f.ElementAtOrDefault(1)),
                IpAddress = Blank(f.ElementAtOrDefault(2)),
                Subnet = Blank(f.ElementAtOrDefault(3)),
                Gateway = Blank(f.ElementAtOrDefault(4)),
                DhcpEnabled = bool.TryParse(f.ElementAtOrDefault(5), out var dhcp) ? dhcp : null,
            }),
        };
    }

    /// <summary>
    /// Child collections arrive as '|'-delimited strings (flat string arrays survive PSRP's
    /// depth-1 serialization; arrays of objects do not). A row with no name is skipped.
    /// </summary>
    /// <summary><paramref name="max"/> drops the detection row the wrapper adds beyond the cap.</summary>
    private static List<T> ParseDelimited<T>(PSObject obj, string property, Func<string[], T> map, int? max = null)
    {
        var rows = new List<T>();
        foreach (var entry in PsValue.GetMany(obj, property))
        {
            if (max is { } limit && rows.Count >= limit)
                break;
            if (entry.BaseObject is not string text || string.IsNullOrWhiteSpace(text))
                continue;
            var fields = text.Split('|');
            if (string.IsNullOrWhiteSpace(fields[0]))
                continue;
            rows.Add(map(fields));
        }

        return rows;
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static ScomOptions RequireConfigured(IOptions<ScomOptions> options)
    {
        var scom = options.Value;
        if (!scom.Configured)
            throw new McpException("SCOM tools are not configured: set Scom:ManagementServer to the management server FQDN.");
        return scom;
    }

    private static string Capitalize(string value)
        => char.ToUpperInvariant(value[0]) + value[1..].ToLowerInvariant();

    internal static ScomAlertDto? MapAlert(PSObject obj)
    {
        var name = PsValue.Get<string>(obj, "Name");
        if (string.IsNullOrEmpty(name))
            return null;

        var description = PsValue.Get<string>(obj, "Description");
        return new ScomAlertDto
        {
            Id = PsValue.Get<string>(obj, "Id") ?? "",
            Name = name,
            Description = description is null ? null : TextUtil.Truncate(description, MaxDescriptionLength),
            Severity = PsValue.Get<string>(obj, "Severity"),
            Priority = PsValue.Get<string>(obj, "Priority"),
            ResolutionState = PsValue.Get<int>(obj, "ResolutionState"),
            MonitoringObject = PsValue.Get<string>(obj, "MonitoringObjectDisplayName"),
            MonitoringObjectPath = PsValue.Get<string>(obj, "MonitoringObjectPath"),
            TimeRaised = ToOffset(PsValue.Get<DateTime>(obj, "TimeRaised")),
            LastModified = ToOffset(PsValue.Get<DateTime>(obj, "LastModified")),
            RepeatCount = PsValue.Get<int>(obj, "RepeatCount"),
        };
    }

    internal static ScomObjectDto? MapObject(PSObject obj)
    {
        var displayName = PsValue.Get<string>(obj, "DisplayName");
        if (string.IsNullOrEmpty(displayName))
            return null;

        return new ScomObjectDto
        {
            DisplayName = displayName,
            FullName = PsValue.Get<string>(obj, "FullName"),
            Path = PsValue.Get<string>(obj, "Path"),
            HealthState = PsValue.Get<string>(obj, "HealthState"),
            IsAvailable = PsValue.Get<bool>(obj, "IsAvailable"),
            InMaintenanceMode = PsValue.Get<bool>(obj, "InMaintenanceMode"),
            StateLastModified = ToOffset(PsValue.Get<DateTime>(obj, "StateLastModified")),
        };
    }

    /// <summary>
    /// SCOM SDK values reach us with Kind=Unspecified (measured over PSRP for TimeRaised,
    /// LastModified and StateLastModified) but they are UTC — SCOM stores UTC and only the
    /// Operations Console converts for display. The implicit DateTime→DateTimeOffset
    /// conversion treats Unspecified as local, which would stamp the MCP host's offset and
    /// put every SCOM timestamp in a different frame from the UTC window the wrapper filtered
    /// on. Kind=Local (what Get-WinEvent yields) already carries the right instant, so it is
    /// converted, not relabelled. default(DateTime) is null, not a value: converting it under
    /// a positive offset would underflow DateTimeOffset.
    /// </summary>
    private static DateTimeOffset? ToOffset(DateTime value)
        => value == default
            ? null
            : new DateTimeOffset(value.Kind == DateTimeKind.Unspecified
                ? DateTime.SpecifyKind(value, DateTimeKind.Utc)
                : value).ToUniversalTime();
}
