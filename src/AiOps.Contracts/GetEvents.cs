namespace AiOps.Contracts;

/// <summary>Per-server outcome of a get_events call.</summary>
public sealed record ServerEventsResult
{
    public required string Server { get; init; }

    /// <summary>ok | unreachable | error</summary>
    public required string Status { get; init; }

    public string? Error { get; init; }

    public IReadOnlyList<EventRecordDto> Events { get; init; } = [];
}

public sealed record EventRecordDto
{
    public required DateTimeOffset TimeCreated { get; init; }
    public required int Id { get; init; }
    public string? Level { get; init; }
    public string? Provider { get; init; }
    /// <summary>Truncated to the configured maximum length (default 500 chars).</summary>
    public string? Message { get; init; }
}

public sealed record GetEventsResult
{
    public required IReadOnlyList<ServerEventsResult> Results { get; init; }
}
