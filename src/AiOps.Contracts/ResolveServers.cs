namespace AiOps.Contracts;

public sealed record ResolveServersResult
{
    public required IReadOnlyList<ResolvedServer> Servers { get; init; }

    /// <summary>True when results were cut at the hard cap.</summary>
    public bool Truncated { get; init; }
}

public sealed record ResolvedServer
{
    /// <summary>FQDN when AD has one, otherwise the computer account name.</summary>
    public required string Name { get; init; }

    public string? OperatingSystem { get; init; }

    /// <summary>Distinguished name (shows the OU the account lives in).</summary>
    public string? DistinguishedName { get; init; }
}
