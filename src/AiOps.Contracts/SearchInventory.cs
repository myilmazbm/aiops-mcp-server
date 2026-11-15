namespace AiOps.Contracts;

public sealed record SearchInventoryResult
{
    public required IReadOnlyList<InventorySearchHit> Results { get; init; }
}

public sealed record InventorySearchHit
{
    public required string Server { get; init; }

    /// <summary>Cosine similarity score from the vector index (higher is closer).</summary>
    public required float Score { get; init; }

    /// <summary>Truncated inventory summary of the matching server.</summary>
    public string? Summary { get; init; }

    public DateTimeOffset? CollectedAt { get; init; }
}
