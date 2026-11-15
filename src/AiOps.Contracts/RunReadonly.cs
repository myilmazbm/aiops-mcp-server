namespace AiOps.Contracts;

public sealed record RunReadonlyResult
{
    public required string Server { get; init; }

    /// <summary>ok | unreachable | error</summary>
    public required string Status { get; init; }

    public string? Error { get; init; }

    /// <summary>PowerShell FullyQualifiedErrorId when the command failed.</summary>
    public string? ErrorId { get; init; }

    /// <summary>True when output was cut at the hard row cap.</summary>
    public bool Truncated { get; init; }

    /// <summary>
    /// One entry per output object: property name → formatted value. Primitive outputs
    /// use a single "value" key.
    /// </summary>
    public IReadOnlyList<IReadOnlyDictionary<string, string?>> Rows { get; init; } = [];
}
