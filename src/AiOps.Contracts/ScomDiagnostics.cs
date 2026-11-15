namespace AiOps.Contracts;

/// <summary>
/// Root-cause chain for one alert: what raised it, which management pack owns that workflow,
/// the vendor's own cause/resolution text, and the overrides actually in effect. This is the
/// answer to "why did this fire, and why here".
/// </summary>
public sealed record GetScomAlertDetailResult
{
    /// <summary>ok | notfound | unreachable | error</summary>
    public required string Status { get; init; }

    public string? Error { get; init; }

    public ScomAlertDetailDto? Alert { get; init; }
}

public sealed record ScomAlertDetailDto
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public string? Severity { get; init; }
    public int ResolutionState { get; init; }
    public string? MonitoringObject { get; init; }
    /// <summary>Current health of the object the alert was raised against.</summary>
    public string? MonitoringObjectHealthState { get; init; }
    public DateTimeOffset? TimeRaised { get; init; }

    /// <summary>Monitor | Rule — which kind of workflow raised the alert.</summary>
    public string? SourceKind { get; init; }
    public string? SourceName { get; init; }
    /// <summary>The class the workflow targets, e.g. "MSSQL on Windows: DB Engine".</summary>
    public string? TargetClass { get; init; }
    public string? ManagementPack { get; init; }
    public string? ManagementPackVersion { get; init; }

    /// <summary>Vendor cause/resolution text, tags stripped and truncated.</summary>
    public string? KnowledgeArticle { get; init; }

    /// <summary>Overrides in effect on the workflow — the usual reason a threshold differs or nothing alerted.</summary>
    public IReadOnlyList<ScomOverrideDto> Overrides { get; init; } = [];

    /// <summary>Resolution timeline, oldest first.</summary>
    public IReadOnlyList<ScomAlertHistoryDto> History { get; init; } = [];
}

public sealed record ScomOverrideDto
{
    /// <summary>The overridden field: a property (Enabled, GenerateAlert) or a configuration parameter (Threshold).</summary>
    public required string Field { get; init; }
    public string? Value { get; init; }
    /// <summary>What the override is scoped to — a class, group or instance.</summary>
    public string? Target { get; init; }
    public string? ManagementPack { get; init; }
    public bool Enforced { get; init; }
}

public sealed record ScomAlertHistoryDto
{
    public DateTimeOffset? At { get; init; }
    public string? ModifiedBy { get; init; }
    public int ResolutionState { get; init; }
    public string? Comment { get; init; }
}

/// <summary>
/// Per-monitor health timeline. The agentless WinRM path cannot reproduce this: it is SCOM's
/// own record of when a monitor flipped and in which direction.
/// </summary>
public sealed record GetScomStateChangesResult
{
    /// <summary>ok | unreachable | error</summary>
    public required string Status { get; init; }

    public string? Error { get; init; }

    public bool Truncated { get; init; }

    public DateTimeOffset? WindowStart { get; init; }
    public DateTimeOffset? WindowEnd { get; init; }

    public IReadOnlyList<ScomStateChangeDto> Changes { get; init; } = [];
}

public sealed record ScomStateChangeDto
{
    public required string Server { get; init; }
    public string? MonitoringObject { get; init; }
    public required string Monitor { get; init; }
    public DateTimeOffset? At { get; init; }
    public string? FromState { get; init; }
    public string? ToState { get; init; }
}

/// <summary>
/// Whether the monitoring system itself is healthy and complete — grey agents, heartbeat
/// configuration, maintenance mode, management-server health, coverage gaps. Answers the
/// question behind every silent incident: "is SCOM even watching this?"
/// </summary>
public sealed record GetScomMonitoringHealthResult
{
    /// <summary>ok | partial | unreachable | error</summary>
    public required string Status { get; init; }

    public string? Error { get; init; }

    /// <summary>
    /// Parts the service account's SCOM role could not read. The account is a Read-Only
    /// Operator by design, and agent details, management-server details and the settings
    /// below need the SCOM Administrator role. When this is non-empty the status is
    /// "partial": the affected settings are null (not zero), and agents and management
    /// servers carry name and health only. Everything else is real.
    /// </summary>
    public IReadOnlyList<string> Unavailable { get; init; } = [];

    public string? ManagementGroup { get; init; }
    public string? ManagementGroupVersion { get; init; }
    public bool IsConnected { get; init; }

    public int AgentCount { get; init; }
    /// <summary>Agents whose watcher is not healthy — i.e. SCOM has stopped hearing from them.</summary>
    public int GreyAgentCount { get; init; }
    public int? PendingAgentCount { get; init; }
    /// <summary>Zero means nobody is being notified of anything, however well monitoring works.</summary>
    public int? NotificationSubscriptionCount { get; init; }

    public int? HeartbeatIntervalSeconds { get; init; }
    public int? MissingHeartbeatThreshold { get; init; }
    /// <summary>Retention for collected performance data; bounds get_scom_performance.</summary>
    public int? PerformanceRetentionDays { get; init; }

    /// <summary>Retention for monitor state-change history; bounds get_scom_state_changes.</summary>
    public int? StateChangeRetentionDays { get; init; }

    /// <summary>True when any list below was cut at the row cap.</summary>
    public bool Truncated { get; init; }

    public IReadOnlyList<ScomManagementServerDto> ManagementServers { get; init; } = [];
    public IReadOnlyList<ScomAgentDto> Agents { get; init; } = [];
    public IReadOnlyList<ScomAgentDto> GreyAgents { get; init; } = [];
    public IReadOnlyList<ScomMaintenanceDto> InMaintenanceMode { get; init; } = [];
    /// <summary>Discovered computers with no agent. The management server itself legitimately has none.</summary>
    public IReadOnlyList<string> ComputersWithoutAgent { get; init; } = [];
}

public sealed record ScomManagementServerDto
{
    public required string Name { get; init; }
    public string? HealthState { get; init; }
    public string? Version { get; init; }
    public bool IsRootManagementServer { get; init; }
}

public sealed record ScomAgentDto
{
    public required string Name { get; init; }
    public string? HealthState { get; init; }
    public string? Version { get; init; }
    public string? PrimaryManagementServer { get; init; }
    public bool? ProxyingEnabled { get; init; }
    public bool? IsAvailable { get; init; }
    public DateTimeOffset? StateLastModified { get; init; }
}

public sealed record ScomMaintenanceDto
{
    public required string Object { get; init; }
    public DateTimeOffset? Start { get; init; }
    public DateTimeOffset? ScheduledEnd { get; init; }
    public string? Reason { get; init; }
    public string? User { get; init; }
}
