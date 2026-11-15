namespace AiOps.Contracts;

public sealed record GetScomPerformanceResult
{
    /// <summary>ok | unreachable | error</summary>
    public required string Status { get; init; }

    public string? Error { get; init; }

    /// <summary>True when more series matched than the hard cap returned.</summary>
    public bool Truncated { get; init; }

    /// <summary>The time window actually queried (UTC).</summary>
    public DateTimeOffset? WindowStart { get; init; }
    public DateTimeOffset? WindowEnd { get; init; }

    public IReadOnlyList<ScomPerformanceSeriesDto> Series { get; init; } = [];
}

/// <summary>
/// One counter series as SCOM recorded it, already aggregated on the management server.
/// Unlike get_counters (live sampling over WinRM) this is historical and also covers
/// hosts the fleet JEA endpoint cannot reach, such as domain controllers.
/// </summary>
public sealed record ScomPerformanceSeriesDto
{
    /// <summary>The monitored host this series belongs to.</summary>
    public required string Server { get; init; }

    /// <summary>The monitored object the counter hangs off (a disk, the OS, a health service…).</summary>
    public string? MonitoringObject { get; init; }

    /// <summary>"PerfObject\Counter", e.g. "Processor Information\% Processor Time".</summary>
    public required string Counter { get; init; }

    /// <summary>Counter instance, e.g. "_Total" or "C:"; null when the counter has none.</summary>
    public string? Instance { get; init; }

    public required int SampleCount { get; init; }
    public required double Min { get; init; }
    public required double Avg { get; init; }
    public required double Max { get; init; }
    public required double Last { get; init; }
    public DateTimeOffset? LastSampledAt { get; init; }

    /// <summary>Raw samples, newest last; only populated when explicitly requested.</summary>
    public IReadOnlyList<CounterSampleDto>? Samples { get; init; }
}

public sealed record GetScomInventoryResult
{
    /// <summary>ok | unreachable | error</summary>
    public required string Status { get; init; }

    public string? Error { get; init; }

    public bool Truncated { get; init; }

    public IReadOnlyList<ScomComputerDto> Computers { get; init; } = [];
}

/// <summary>
/// What SCOM discovered about a computer, without logging into it. This is the only
/// inventory source for hosts outside the fleet JEA scope (domain controllers).
/// </summary>
public sealed record ScomComputerDto
{
    public required string Computer { get; init; }
    public string? HealthState { get; init; }
    public bool InMaintenanceMode { get; init; }

    /// <summary>
    /// Discovered properties in one bag: the computer's own (DNS name, IP, OU, forest, SID,
    /// virtual-machine flag, CPU count), the operating system's under an "OS." prefix
    /// (version, build, install date, physical memory), and — on domain controllers only —
    /// the directory role's under an "AD." prefix (all five FSMO holders, global-catalog and
    /// read-only flags).
    /// </summary>
    public IReadOnlyDictionary<string, string> Properties { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<ScomDiskDto> Disks { get; init; } = [];

    public IReadOnlyList<ScomNetworkAdapterDto> NetworkAdapters { get; init; } = [];
}

/// <summary>Disk geometry from discovery. Free space is not discovered — read it from get_scom_performance.</summary>
public sealed record ScomDiskDto
{
    public required string Name { get; init; }
    public string? FileSystem { get; init; }
    public double? SizeMb { get; init; }
    public string? HealthState { get; init; }
}

public sealed record ScomNetworkAdapterDto
{
    public required string Name { get; init; }
    public string? MacAddress { get; init; }
    public string? IpAddress { get; init; }
    public string? Subnet { get; init; }
    public string? Gateway { get; init; }
    public bool? DhcpEnabled { get; init; }
}
