using AiOps.Mcp.Inventory;
using AiOps.Mcp.Inventory.Search;
using AiOps.Mcp.Tools.SearchInventory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using NSubstitute;

namespace AiOps.Mcp.Tests;

public class SearchInventoryToolTests
{
    private readonly IEmbeddingClient _embedder = Substitute.For<IEmbeddingClient>();
    private readonly IVectorIndex _index = Substitute.For<IVectorIndex>();

    private static readonly InventoryOptions Configured = new()
    {
        EmbeddingEndpoint = "http://vllm:8000",
        QdrantEndpoint = "http://qdrant:6334",
    };

    private Task<AiOps.Contracts.SearchInventoryResult> Call(string query, InventoryOptions? options = null)
        => SearchInventoryTool.SearchInventory(
            _embedder, _index, Options.Create(options ?? Configured), NullLoggerFactory.Instance, query);

    [Fact]
    public async Task Embeds_the_trimmed_query_and_returns_ranked_hits()
    {
        var vector = new[] { 0.5f, 0.5f };
        _embedder.EmbedAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns([vector]);
        _index.SearchAsync(vector, SearchInventoryTool.ResultLimit, Arg.Any<CancellationToken>())
            .Returns([
                new VectorHit("SQL01", 0.92f, "server: SQL01", new DateTimeOffset(2026, 7, 6, 2, 0, 0, TimeSpan.Zero)),
                new VectorHit("SQL02", 0.87f, "server: SQL02", null),
            ]);

        var result = await Call("  sql server 2019 machines  ");

        await _embedder.Received(1).EmbedAsync(
            Arg.Is<IReadOnlyList<string>>(t => t.Count == 1 && t[0] == "sql server 2019 machines"),
            Arg.Any<CancellationToken>());
        Assert.Equal(2, result.Results.Count);
        Assert.Equal("SQL01", result.Results[0].Server);
        Assert.Equal(0.92f, result.Results[0].Score);
        Assert.NotNull(result.Results[0].CollectedAt);
    }

    [Fact]
    public async Task Returned_summaries_are_truncated()
    {
        _embedder.EmbedAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns([new[] { 1f }]);
        _index.SearchAsync(Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([new VectorHit("SRV1", 0.5f, new string('s', 5000), null)]);

        var result = await Call("anything");

        Assert.Equal(SearchInventoryTool.MaxSummaryLength, result.Results[0].Summary!.Length);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Rejects_blank_query(string query)
        => await Assert.ThrowsAsync<McpException>(() => Call(query));

    [Fact]
    public async Task Unconfigured_search_fails_with_guidance_before_calling_anything()
    {
        var ex = await Assert.ThrowsAsync<McpException>(
            () => Call("query", new InventoryOptions()));

        Assert.Contains("EmbeddingEndpoint", ex.Message);
        await _embedder.DidNotReceiveWithAnyArgs().EmbedAsync(default!, default);
        await _index.DidNotReceiveWithAnyArgs().SearchAsync(default!, default, default);
    }
}
