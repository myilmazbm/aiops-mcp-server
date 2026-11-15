namespace AiOps.Contracts;

public sealed record GetCountersResult
{
    public required IReadOnlyList<ServerCountersResult> Results { get; init; }
}

/// <summary>Per-server outcome of a get_counters call.</summary>
public sealed record ServerCountersResult
{
    public required string Server { get; init; }

    /// <summary>ok | partial | unreachable | error</summary>
    public required string Status { get; init; }

    public string? Error { get; init; }

    /// <summary>True when the server returned more counter series than the cap allowed.</summary>
    public bool Truncated { get; init; }

    public IReadOnlyList<CounterStatsDto> Counters { get; init; } = [];
}

public sealed record CounterStatsDto
{
    /// <summary>The counter path as requested (English).</summary>
    public required string Path { get; init; }

    /// <summary>The localized path actually queried on the target, when it differs.</summary>
    public string? LocalizedPath { get; init; }

    public required double Min { get; init; }
    public required double Max { get; init; }
    public required double Avg { get; init; }
    public required double Last { get; init; }
    public required int SampleCount { get; init; }

    /// <summary>Raw samples; only populated when explicitly requested.</summary>
    public IReadOnlyList<CounterSampleDto>? Samples { get; init; }
}

public sealed record CounterSampleDto
{
    public required DateTimeOffset Timestamp { get; init; }
    public required double Value { get; init; }
}
