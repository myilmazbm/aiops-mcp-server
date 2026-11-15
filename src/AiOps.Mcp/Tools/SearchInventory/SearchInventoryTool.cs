using System.ComponentModel;
using System.Diagnostics;
using AiOps.Contracts;
using AiOps.Mcp.Inventory;
using AiOps.Mcp.Inventory.Search;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace AiOps.Mcp.Tools.SearchInventory;

[McpServerToolType]
public static class SearchInventoryTool
{
    public const int ResultLimit = 10;
    /// <summary>Returned summaries are cut to protect the LLM context window.</summary>
    public const int MaxSummaryLength = 600;

    [McpServerTool(Name = "search_inventory", ReadOnly = true)]
    [Description("Semantic search across the fleet's inventory snapshots (services, software, ports, GPOs, hotfixes, certificates, tasks). Ask in natural language, e.g. 'servers running SQL Server 2019' or 'machines with certificates expiring soon'. Returns the closest servers with truncated inventory summaries.")]
    public static async Task<SearchInventoryResult> SearchInventory(
        IEmbeddingClient embeddingClient,
        IVectorIndex vectorIndex,
        IOptions<InventoryOptions> options,
        ILoggerFactory loggerFactory,
        [Description("Natural-language query about fleet inventory")] string query,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            throw new McpException("query is required.");
        if (!options.Value.SearchConfigured)
            throw new McpException(
                "search_inventory is not configured: set Inventory:EmbeddingEndpoint and Inventory:QdrantEndpoint.");

        var stopwatch = Stopwatch.StartNew();
        var vectors = await embeddingClient.EmbedAsync([query.Trim()], cancellationToken).ConfigureAwait(false);
        var hits = await vectorIndex.SearchAsync(vectors[0], ResultLimit, cancellationToken).ConfigureAwait(false);

        loggerFactory.CreateLogger("AiOps.Mcp.Tools.search_inventory").LogInformation(
            "search_inventory queryLength={QueryLength} hits={HitCount} took {ElapsedMs} ms",
            query.Length, hits.Count, stopwatch.ElapsedMilliseconds);

        return new SearchInventoryResult
        {
            Results = hits.Select(hit => new InventorySearchHit
            {
                Server = hit.Server,
                Score = hit.Score,
                Summary = hit.Summary is null ? null : TextUtil.Truncate(hit.Summary, MaxSummaryLength),
                CollectedAt = hit.CollectedAt,
            }).ToList(),
        };
    }
}
