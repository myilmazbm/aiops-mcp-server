using System.Net;
using System.Text;
using System.Text.Json;
using AiOps.Mcp.Inventory;
using AiOps.Mcp.Inventory.Search;
using Microsoft.Extensions.Options;

namespace AiOps.Mcp.Tests;

public class EmbeddingClientTests
{
    private sealed class FakeHandler(Func<HttpRequestMessage, string, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return responder(request, LastBody ?? "");
        }
    }

    private static HttpResponseMessage Json(object payload)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
        };

    /// <summary>Factory over a fixed handler; counts CreateClient calls to prove one client per request.</summary>
    private sealed class FakeFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public int Created { get; private set; }

        public HttpClient CreateClient(string name)
        {
            Created++;
            return new HttpClient(handler, disposeHandler: false);
        }
    }

    private static OpenAiCompatibleEmbeddingClient CreateClient(FakeHandler handler, string endpoint = "http://vllm:8000")
        => new(
            new FakeFactory(handler),
            // EmbeddingDimensions = 0 disables the exact-dimension check for the tests that
            // use short synthetic vectors; the dimension-mismatch test sets it explicitly.
            Options.Create(new InventoryOptions
            {
                EmbeddingEndpoint = endpoint,
                EmbeddingModel = "bge-m3",
                EmbeddingDimensions = 0,
            }));

    [Theory]
    [InlineData("http://vllm:8000", "http://vllm:8000/v1/embeddings")]
    [InlineData("http://vllm:8000/", "http://vllm:8000/v1/embeddings")]
    [InlineData("http://vllm:8000/v1", "http://vllm:8000/v1/embeddings")]
    [InlineData("http://vllm:8000/v1/", "http://vllm:8000/v1/embeddings")]
    public void Endpoint_root_and_v1_configurations_build_the_same_url(string configured, string expected)
        => Assert.Equal(expected, OpenAiCompatibleEmbeddingClient.BuildEmbeddingsUrl(configured));

    [Fact]
    public async Task Sends_model_and_inputs_to_the_embeddings_route()
    {
        var handler = new FakeHandler((_, _) => Json(new
        {
            data = new[] { new { index = 0, embedding = new[] { 0.1f, 0.2f } } },
        }));

        await CreateClient(handler).EmbedAsync(["hello fleet"], CancellationToken.None);

        Assert.Equal("http://vllm:8000/v1/embeddings", handler.LastRequest!.RequestUri!.ToString());
        using var body = JsonDocument.Parse(handler.LastBody!);
        Assert.Equal("bge-m3", body.RootElement.GetProperty("model").GetString());
        Assert.Equal("hello fleet", body.RootElement.GetProperty("input")[0].GetString());
    }

    [Fact]
    public async Task Orders_vectors_by_index_not_response_position()
    {
        var handler = new FakeHandler((_, _) => Json(new
        {
            data = new[]
            {
                new { index = 1, embedding = new[] { 2f } },
                new { index = 0, embedding = new[] { 1f } },
            },
        }));

        var vectors = await CreateClient(handler).EmbedAsync(["first", "second"], CancellationToken.None);

        Assert.Equal(1f, vectors[0][0]);
        Assert.Equal(2f, vectors[1][0]);
    }

    [Fact]
    public async Task Vector_count_mismatch_is_an_error()
    {
        var handler = new FakeHandler((_, _) => Json(new
        {
            data = new[] { new { index = 0, embedding = new[] { 1f } } },
        }));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => CreateClient(handler).EmbedAsync(["a", "b"], CancellationToken.None));

        Assert.Contains("1 vectors for 2 inputs", ex.Message);
    }

    [Fact]
    public async Task Empty_embedding_is_an_error()
    {
        var handler = new FakeHandler((_, _) => Json(new
        {
            data = new[] { new { index = 0, embedding = Array.Empty<float>() } },
        }));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => CreateClient(handler).EmbedAsync(["a"], CancellationToken.None));
    }

    [Fact]
    public async Task Http_error_surfaces_status_and_truncated_body()
    {
        var handler = new FakeHandler((_, _) => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent(new string('e', 5000)),
        });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => CreateClient(handler).EmbedAsync(["a"], CancellationToken.None));

        Assert.Contains("500", ex.Message);
        Assert.True(ex.Message.Length < 500);
    }

    [Fact]
    public async Task Each_call_takes_a_fresh_client_from_the_factory()
    {
        // The service is a singleton captured by the singleton indexer; a single injected
        // HttpClient would pin one handler forever and defeat handler rotation.
        var handler = new FakeHandler((_, _) => Json(new
        {
            data = new[] { new { index = 0, embedding = new[] { 1f } } },
        }));
        var factory = new FakeFactory(handler);
        var client = new OpenAiCompatibleEmbeddingClient(factory, Options.Create(new InventoryOptions
        {
            EmbeddingEndpoint = "http://vllm:8000",
            EmbeddingDimensions = 0,
        }));

        await client.EmbedAsync(["a"], CancellationToken.None);
        await client.EmbedAsync(["b"], CancellationToken.None);

        Assert.Equal(2, factory.Created);
    }

    [Fact]
    public async Task Vector_of_unexpected_dimension_is_rejected()
    {
        var handler = new FakeHandler((_, _) => Json(new
        {
            data = new[] { new { index = 0, embedding = new[] { 1f, 2f, 3f } } },
        }));
        var client = new OpenAiCompatibleEmbeddingClient(
            new FakeFactory(handler),
            Options.Create(new InventoryOptions
            {
                EmbeddingEndpoint = "http://vllm:8000",
                EmbeddingDimensions = 1024, // model contract; a 3-dim response is malformed
            }));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.EmbedAsync(["a"], CancellationToken.None));

        Assert.Contains("expected 1024", ex.Message);
    }

    [Fact]
    public async Task Unconfigured_endpoint_fails_fast()
    {
        var handler = new FakeHandler((_, _) => throw new InvalidOperationException("must not be called"));
        var client = new OpenAiCompatibleEmbeddingClient(
            new FakeFactory(handler), Options.Create(new InventoryOptions()));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.EmbedAsync(["a"], CancellationToken.None));

        Assert.Null(handler.LastRequest);
    }
}
