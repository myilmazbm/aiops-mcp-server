namespace AiOps.Mcp.Inventory;

public sealed class InventoryOptions
{
    public const string SectionName = "Inventory";

    /// <summary>
    /// Integrated-security connection string (no user/password — the service's gMSA
    /// authenticates). Empty disables persistence; get_inventory still works live.
    /// </summary>
    public string? SqlConnectionString { get; set; }

    /// <summary>
    /// Fleet servers swept nightly. Explicit for now; resolve_servers (AD) will feed
    /// this in a later roadmap step.
    /// </summary>
    public string[] Servers { get; set; } = [];

    /// <summary>Local hour (0-23) at which the nightly sweep starts.</summary>
    public int NightlyHourLocal { get; set; } = 2;

    /// <summary>
    /// Base URL of an OpenAI-compatible embedding service (vLLM serving bge-m3).
    /// Empty disables semantic indexing and search_inventory.
    /// </summary>
    public string? EmbeddingEndpoint { get; set; }

    public string EmbeddingModel { get; set; } = "bge-m3";

    /// <summary>Expected embedding dimension; a response vector of any other length is rejected. bge-m3 = 1024.</summary>
    public int EmbeddingDimensions { get; set; } = 1024;

    /// <summary>Qdrant URL, e.g. http://qdrant:6334. Empty disables semantic search.</summary>
    public string? QdrantEndpoint { get; set; }

    public string QdrantCollection { get; set; } = "aiops-inventory";

    public bool SearchConfigured => !string.IsNullOrEmpty(EmbeddingEndpoint) && !string.IsNullOrEmpty(QdrantEndpoint);
}
