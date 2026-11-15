using AiOps.Contracts;

namespace AiOps.Mcp.Inventory.Search;

public interface IInventoryIndexer
{
    /// <summary>Embeds a snapshot summary and upserts it into the vector index.</summary>
    Task IndexAsync(InventorySnapshot snapshot, CancellationToken cancellationToken);
}

/// <summary>Used when embedding/Qdrant is not configured; the sweep then only writes SQL.</summary>
public sealed class NoOpInventoryIndexer : IInventoryIndexer
{
    public Task IndexAsync(InventorySnapshot snapshot, CancellationToken cancellationToken) => Task.CompletedTask;
}

public sealed class QdrantInventoryIndexer(IEmbeddingClient embeddingClient, IVectorIndex vectorIndex) : IInventoryIndexer
{
    public async Task IndexAsync(InventorySnapshot snapshot, CancellationToken cancellationToken)
    {
        var summary = InventorySummaryBuilder.Build(snapshot);
        var vectors = await embeddingClient.EmbedAsync([summary], cancellationToken).ConfigureAwait(false);
        await vectorIndex.UpsertAsync(snapshot.Server, vectors[0], summary, snapshot.CollectedAt, cancellationToken)
            .ConfigureAwait(false);
    }
}
