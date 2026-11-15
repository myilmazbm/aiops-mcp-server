using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace AiOps.Mcp.Inventory.Search;

public interface IEmbeddingClient
{
    /// <summary>Embeds texts in order; result[i] corresponds to texts[i].</summary>
    Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken);
}

/// <summary>
/// Client for an OpenAI-compatible /v1/embeddings endpoint (vLLM serving bge-m3).
/// The endpoint is an internal service; no API key is sent — authentication, if any,
/// is network-level. No credentials live in code or config.
/// </summary>
public sealed class OpenAiCompatibleEmbeddingClient(IHttpClientFactory httpClientFactory, IOptions<InventoryOptions> options)
    : IEmbeddingClient
{
    public const string HttpClientName = "embeddings";

    

    private readonly InventoryOptions _options = options.Value;

    private sealed record EmbeddingRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("input")] IReadOnlyList<string> Input);

    private sealed record EmbeddingResponse([property: JsonPropertyName("data")] List<EmbeddingItem>? Data);

    private sealed record EmbeddingItem(
        [property: JsonPropertyName("index")] int Index,
        [property: JsonPropertyName("embedding")] float[]? Embedding);

    public async Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(_options.EmbeddingEndpoint))
            throw new InvalidOperationException("Inventory:EmbeddingEndpoint is not configured.");

        var uri = new Uri(BuildEmbeddingsUrl(_options.EmbeddingEndpoint), UriKind.Absolute);
        // A client per call from the factory: this service is a singleton (captured by the
        // singleton indexer), so a single injected HttpClient would pin one handler forever
        // and defeat the factory's handler rotation / DNS refresh.
        using var httpClient = httpClientFactory.CreateClient(HttpClientName);
        using var response = await httpClient.PostAsJsonAsync(
            uri, new EmbeddingRequest(_options.EmbeddingModel, texts), JsonSerializerOptions.Web, cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException(
                $"Embedding endpoint returned {(int)response.StatusCode}: {TextUtil.Truncate(body, 300)}");
        }

        var parsed = await response.Content.ReadFromJsonAsync<EmbeddingResponse>(JsonSerializerOptions.Web, cancellationToken)
            .ConfigureAwait(false);

        if (parsed?.Data is not { Count: > 0 } data || data.Count != texts.Count)
            throw new InvalidOperationException(
                $"Embedding endpoint returned {parsed?.Data?.Count ?? 0} vectors for {texts.Count} inputs.");

        // The API does not guarantee response order; index is authoritative.
        var expectedDim = _options.EmbeddingDimensions;
        var vectors = new float[texts.Count][];
        foreach (var item in data)
        {
            if (item.Index < 0 || item.Index >= texts.Count || item.Embedding is not { Length: > 0 })
                throw new InvalidOperationException("Embedding endpoint returned a malformed vector.");
            if (expectedDim > 0 && item.Embedding.Length != expectedDim)
                throw new InvalidOperationException(
                    $"Embedding endpoint returned a {item.Embedding.Length}-dimension vector; expected {expectedDim}.");
            vectors[item.Index] = item.Embedding;
        }

        if (vectors.Any(v => v is null))
            throw new InvalidOperationException("Embedding endpoint response is missing vector indices.");

        return vectors;
    }

    /// <summary>
    /// The configured endpoint is the service root; "/v1/embeddings" is appended. A root
    /// already ending in /v1 is tolerated so both common configurations work.
    /// </summary>
    internal static string BuildEmbeddingsUrl(string endpoint)
    {
        var root = endpoint.TrimEnd('/');
        return root.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)
            ? root + "/embeddings"
            : root + "/v1/embeddings";
    }
}
