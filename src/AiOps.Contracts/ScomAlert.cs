namespace AiOps.Contracts;

/// <summary>
/// Normalized SCOM alert as accepted by the webhook endpoint and forwarded to the
/// (separate) orchestrator service. Nothing is persisted; the queue is in-memory only.
/// </summary>
public sealed record ScomAlert
{
    public required string AlertId { get; init; }

    public required string Name { get; init; }

    /// <summary>Truncated at ingestion; raw SCOM descriptions can carry large PII-laden dumps.</summary>
    public string? Description { get; init; }

    /// <summary>Critical | Warning | Information</summary>
    public string? Severity { get; init; }

    public string? Priority { get; init; }

    /// <summary>The monitoring object's server, when derivable.</summary>
    public string? Server { get; init; }

    public DateTimeOffset? TimeRaised { get; init; }

    /// <summary>When the webhook accepted the alert (UTC).</summary>
    public required DateTimeOffset ReceivedAt { get; init; }
}
