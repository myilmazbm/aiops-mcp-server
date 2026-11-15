namespace AiOps.Contracts;

public sealed record GetInventoryResult
{
    public required string Server { get; init; }

    /// <summary>ok | partial | unreachable | error</summary>
    public required string Status { get; init; }

    /// <summary>Per-section error messages for sections that failed.</summary>
    public IReadOnlyDictionary<string, string>? SectionErrors { get; init; }

    /// <summary>Sections whose row count hit the hard cap and were cut off.</summary>
    public IReadOnlyList<string>? TruncatedSections { get; init; }

    public required InventorySnapshot Snapshot { get; init; }
}

/// <summary>
/// One server's inventory. Only requested sections are populated; the same shape is
/// persisted (per-section JSON) by the nightly sweep. No history is kept.
/// </summary>
public sealed record InventorySnapshot
{
    public required string Server { get; init; }
    public required DateTimeOffset CollectedAt { get; init; }
    public IReadOnlyList<ServiceDto>? Services { get; init; }
    public IReadOnlyList<SoftwareDto>? Software { get; init; }
    public IReadOnlyList<ListeningPortDto>? Ports { get; init; }
    public IReadOnlyList<GpoDto>? Gpo { get; init; }
    public IReadOnlyList<HotfixDto>? Hotfix { get; init; }
    public IReadOnlyList<CertificateDto>? Certs { get; init; }
    public IReadOnlyList<ScheduledTaskDto>? Tasks { get; init; }
}

public sealed record ServiceDto
{
    public required string Name { get; init; }
    public string? DisplayName { get; init; }
    public string? Status { get; init; }
    public string? StartType { get; init; }
}

public sealed record SoftwareDto
{
    public required string Name { get; init; }
    public string? Version { get; init; }
    public string? Publisher { get; init; }
    public DateOnly? InstallDate { get; init; }
}

public sealed record ListeningPortDto
{
    public required int Port { get; init; }
    public required string Address { get; init; }
    public int? Pid { get; init; }
    public string? ProcessName { get; init; }
}

public sealed record GpoDto
{
    public required string Name { get; init; }
    public string? Id { get; init; }
}

public sealed record HotfixDto
{
    public required string Id { get; init; }
    public string? Description { get; init; }
    public DateOnly? InstalledOn { get; init; }
}

public sealed record CertificateDto
{
    public required string Thumbprint { get; init; }
    public string? Subject { get; init; }
    public string? Issuer { get; init; }
    public DateTimeOffset? NotAfter { get; init; }
}

public sealed record ScheduledTaskDto
{
    public required string Name { get; init; }
    public string? Path { get; init; }
    public string? State { get; init; }
}
