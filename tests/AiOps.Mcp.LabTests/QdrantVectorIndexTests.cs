using AiOps.Mcp.Inventory;
using AiOps.Mcp.Inventory.Search;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Qdrant.Client;

namespace AiOps.Mcp.LabTests;

/// <summary>
/// Real-Qdrant tests for the vector index. Requires a local Qdrant (gRPC on 6334) or
/// AIOPS_QDRANT_ENDPOINT pointing at one; skipped otherwise. Not run in CI.
/// </summary>
public sealed class QdrantVectorIndexTests : IAsyncLifetime
{
    private static readonly string Endpoint =
        Environment.GetEnvironmentVariable("AIOPS_QDRANT_ENDPOINT") ?? "http://127.0.0.1:6334";

    private readonly string _collection = $"labtest-{Guid.NewGuid():N}";
    private bool _available;

    private QdrantVectorIndex CreateIndex()
        => new(
            Options.Create(new InventoryOptions { QdrantEndpoint = Endpoint, QdrantCollection = _collection }),
            NullLogger<QdrantVectorIndex>.Instance);

    public async Task InitializeAsync()
    {
        try
        {
            var uri = new Uri(Endpoint);
            using var client = new QdrantClient(uri.Host, uri.Port);
            await client.HealthAsync();
            _available = true;
        }
        catch (Exception)
        {
            _available = false;
        }
    }

    public async Task DisposeAsync()
    {
        if (!_available)
            return;

        var uri = new Uri(Endpoint);
        using var client = new QdrantClient(uri.Host, uri.Port);
        try
        {
            await client.DeleteCollectionAsync(_collection);
        }
        catch (Exception)
        {
            // collection may not exist if the test skipped early
        }
    }

    [SkippableFact]
    public async Task Upsert_and_search_round_trip_with_payload()
    {
        Skip.IfNot(_available, "Qdrant not reachable");
        var index = CreateIndex();
        var collectedAt = new DateTimeOffset(2026, 7, 6, 2, 0, 0, TimeSpan.Zero);

        await index.UpsertAsync("SQL01", [1f, 0f, 0f, 0f], "server: SQL01\nsoftware: SQL Server", collectedAt, CancellationToken.None);
        await index.UpsertAsync("WEB01", [0f, 1f, 0f, 0f], "server: WEB01\nsoftware: IIS", collectedAt, CancellationToken.None);

        var hits = await index.SearchAsync([0.9f, 0.1f, 0f, 0f], limit: 2, CancellationToken.None);

        Assert.Equal(2, hits.Count);
        Assert.Equal("SQL01", hits[0].Server); // closest first
        Assert.Contains("SQL Server", hits[0].Summary);
        Assert.Equal(collectedAt, hits[0].CollectedAt);
        Assert.True(hits[0].Score > hits[1].Score);
    }

    [SkippableFact]
    public async Task Reupserting_a_server_in_any_casing_keeps_a_single_point()
    {
        Skip.IfNot(_available, "Qdrant not reachable");
        var index = CreateIndex();
        var now = DateTimeOffset.UtcNow;

        await index.UpsertAsync("srv1.contoso.local", [1f, 0f, 0f, 0f], "old summary", now, CancellationToken.None);
        await index.UpsertAsync("SRV1.CONTOSO.LOCAL", [1f, 0f, 0f, 0f], "new summary", now, CancellationToken.None);

        var hits = await index.SearchAsync([1f, 0f, 0f, 0f], limit: 10, CancellationToken.None);

        var hit = Assert.Single(hits); // one point, not two
        Assert.Equal("new summary", hit.Summary);
    }
}
