namespace AiOps.Mcp.Remoting;

public sealed class RemotingOptions
{
    public const string SectionName = "Remoting";

    /// <summary>JEA session configuration name registered on fleet servers.</summary>
    public string JeaEndpointName { get; set; } = "AIOpsReader";

    public int WinRmPort { get; set; } = 5985;

    // Setters clamp to sane minimums: configuration binds raw values, and 0 would mean a
    // SemaphoreSlim(0) that never admits anyone, a zero timeout, or a pool that evicts
    // every slot it creates.

    /// <summary>Upper bound for concurrent remote invocations across the fleet.</summary>
    public int MaxConcurrency { get => _maxConcurrency; set => _maxConcurrency = Math.Max(1, value); }
    private int _maxConcurrency = 20;

    /// <summary>Per-server timeout; a server exceeding it is reported as unreachable.</summary>
    public int PerServerTimeoutSeconds { get => _perServerTimeoutSeconds; set => _perServerTimeoutSeconds = Math.Max(1, value); }
    private int _perServerTimeoutSeconds = 30;

    /// <summary>Maximum number of persistent sessions kept open (LRU-evicted).</summary>
    public int MaxSessions { get => _maxSessions; set => _maxSessions = Math.Max(1, value); }
    private int _maxSessions = 100;

    /// <summary>Budget for opening a session (WSMan connect + JEA virtual-account startup).</summary>
    public int OpenTimeoutSeconds { get => _openTimeoutSeconds; set => _openTimeoutSeconds = Math.Max(1, value); }
    private int _openTimeoutSeconds = 15;
}
