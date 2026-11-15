namespace AiOps.Mcp.Alerts;

public sealed class AlertsOptions
{
    public const string SectionName = "Alerts";

    /// <summary>
    /// URL of the separate orchestrator service that turns alerts into LLM diagnostics.
    /// Empty means alerts are accepted and logged but not forwarded.
    /// </summary>
    public string? ForwardUrl { get; set; }

    /// <summary>In-memory only — no alert is persisted (architecture invariant).</summary>
    public int QueueCapacity { get; set; } = 1000;

    /// <summary>Requests larger than this are rejected with 413.</summary>
    public int MaxBodyBytes { get; set; } = 64 * 1024;

    /// <summary>
    /// Optional source allow-list (exact IPs). Authentication is network-level (firewall/
    /// IPsec via GPO) — no shared secrets, per the no-credentials-in-config rule.
    /// </summary>
    public string[] AllowedSourceIps { get; set; } = [];

    public int ForwardAttempts { get; set; } = 3;

    public int ForwardRetryDelayMs { get; set; } = 1000;
}
