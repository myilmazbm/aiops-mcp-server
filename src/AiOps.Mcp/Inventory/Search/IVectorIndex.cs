using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Qdrant.Client;
using Qdrant.Client.Grpc;

namespace AiOps.Mcp.Inventory.Search;

public sealed record VectorHit(string Server, float Score, string? Summary, DateTimeOffset? CollectedAt);

public interface IVectorIndex
{
    Task UpsertAsync(string server, float[] vector, string summary, DateTimeOffset collectedAt, CancellationToken cancellationToken);
    Task<IReadOnlyList<VectorHit>> SearchAsync(float[] vector, int limit, CancellationToken cancellationToken);
}

/// <summary>
/// Qdrant-backed index of inventory summary embeddings: one point per server, upserted
/// by deterministic id (no history, mirrors the SQL snapshot semantics).
/// </summary>
public sealed class QdrantVectorIndex(IOptions<InventoryOptions> options, ILogger<QdrantVectorIndex> logger) : IVectorIndex
{
    private readonly InventoryOptions _options = options.Value;
    private readonly Lazy<QdrantClient> _client = new(() =>
    {
        var uri = new Uri(options.Value.QdrantEndpoint!, UriKind.Absolute);
        return new QdrantClient(uri.Host, uri.Port, https: uri.Scheme == "https");
    });

    private readonly SemaphoreSlim _collectionGate = new(1, 1);
    private volatile bool _collectionEnsured;

    /// <summary>
    /// Deterministic point id from the server name. Uppercased invariantly first, so
    /// casing (including Turkish ı → I) never produces a second point for one server.
    /// </summary>
    internal static Guid PointId(string server)
        => new(MD5.HashData(Encoding.UTF8.GetBytes(server.ToUpperInvariant())));

    public async Task UpsertAsync(
        string server, float[] vector, string summary, DateTimeOffset collectedAt, CancellationToken cancellationToken)
    {
        await EnsureCollectionAsync(vector.Length, cancellationToken).ConfigureAwait(false);

        var point = new PointStruct
        {
            Id = PointId(server),
            Vectors = vector,
        };
        point.Payload["server"] = server;
        point.Payload["summary"] = summary;
        point.Payload["collectedAt"] = collectedAt.UtcDateTime.ToString("O");

        await _client.Value.UpsertAsync(_options.QdrantCollection, [point], cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<VectorHit>> SearchAsync(float[] vector, int limit, CancellationToken cancellationToken)
    {
        var hits = await _client.Value.SearchAsync(
            _options.QdrantCollection, vector, limit: (ulong)limit, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        return hits.Select(hit =>
        {
            var payload = hit.Payload;
            return new VectorHit(
                Server: payload.TryGetValue("server", out var s) ? s.StringValue : hit.Id.Uuid,
                Score: hit.Score,
                Summary: payload.TryGetValue("summary", out var sum) ? sum.StringValue : null,
                CollectedAt: payload.TryGetValue("collectedAt", out var c)
                    && DateTimeOffset.TryParse(c.StringValue, null, System.Globalization.DateTimeStyles.RoundtripKind, out var dto)
                    ? dto
                    : null);
        }).ToList();
    }

    private async Task EnsureCollectionAsync(int vectorSize, CancellationToken cancellationToken)
    {
        if (_collectionEnsured)
            return;

        await _collectionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_collectionEnsured)
                return;

            if (!await _client.Value.CollectionExistsAsync(_options.QdrantCollection, cancellationToken).ConfigureAwait(false))
            {
                await _client.Value.CreateCollectionAsync(
                    _options.QdrantCollection,
                    new VectorParams { Size = (ulong)vectorSize, Distance = Distance.Cosine },
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                logger.LogInformation(
                    "Created Qdrant collection {Collection} (dim {Dim}, cosine)", _options.QdrantCollection, vectorSize);
            }

            _collectionEnsured = true;
        }
        finally
        {
            _collectionGate.Release();
        }
    }
}
