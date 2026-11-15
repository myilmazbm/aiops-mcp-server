namespace AiOps.Contracts;

public sealed record GetScomAlertsResult
{
    /// <summary>ok | unreachable | error</summary>
    public required string Status { get; init; }

    public string? Error { get; init; }

    /// <summary>True when more alerts matched than the hard cap returned.</summary>
    public bool Truncated { get; init; }

    public IReadOnlyList<ScomAlertDto> Alerts { get; init; } = [];
}

public sealed record ScomAlertDto
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    /// <summary>Truncated at ingestion (SCOM descriptions can carry large dumps).</summary>
    public string? Description { get; init; }
    /// <summary>Error | Warning | Information</summary>
    public string? Severity { get; init; }
    public string? Priority { get; init; }
    /// <summary>0 = New … 255 = Closed (plus any custom states).</summary>
    public int ResolutionState { get; init; }
    /// <summary>The monitored object the alert was raised for (e.g. a server or a SQL instance).</summary>
    public string? MonitoringObject { get; init; }
    public string? MonitoringObjectPath { get; init; }
    public DateTimeOffset? TimeRaised { get; init; }
    public DateTimeOffset? LastModified { get; init; }
    public int RepeatCount { get; init; }
}

public sealed record GetScomHealthResult
{
    /// <summary>ok | unreachable | error</summary>
    public required string Status { get; init; }

    public string? Error { get; init; }

    public bool Truncated { get; init; }

    public IReadOnlyList<ScomObjectDto> Objects { get; init; } = [];
}

public sealed record ScomObjectDto
{
    public required string DisplayName { get; init; }
    public string? FullName { get; init; }
    public string? Path { get; init; }
    /// <summary>Success | Warning | Error | Uninitialized</summary>
    public string? HealthState { get; init; }
    public bool IsAvailable { get; init; }
    public bool InMaintenanceMode { get; init; }
    public DateTimeOffset? StateLastModified { get; init; }
}
