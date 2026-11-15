using AiOps.Mcp.Inventory;
using AiOps.Mcp.Remoting;
using AiOps.Mcp.Tools.GetInventory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AiOps.Mcp.LabTests;

/// <summary>
/// Integration tests against a real lab server with the AIOpsReader JEA endpoint.
/// Set AIOPS_LAB_SERVER to the target FQDN to enable; skipped otherwise. Not run in CI.
/// </summary>
public class GetInventoryLabTests
{
    private static string? LabServer => Environment.GetEnvironmentVariable("AIOPS_LAB_SERVER");

    [SkippableFact]
    public async Task Collects_all_sections_via_jea_endpoint()
    {
        Skip.If(string.IsNullOrEmpty(LabServer), "AIOPS_LAB_SERVER not set");

        var options = Options.Create(new RemotingOptions());
        using var pool = new PsSessionPool(new WsmanRunspaceConnector(options), options, NullLogger<PsSessionPool>.Instance);
        var executor = new JeaRemoteExecutor(pool, options, NullLogger<JeaRemoteExecutor>.Instance);

        var result = await GetInventoryTool.GetInventory(
            new InventoryCollector(executor), NullLoggerFactory.Instance,
            LabServer!, InventoryCollector.AllSections);

        Assert.NotEqual("unreachable", result.Status);
        Assert.NotEqual("error", result.Status);
        Assert.NotEmpty(result.Snapshot.Services!);
        Assert.NotEmpty(result.Snapshot.Software!);
        Assert.NotEmpty(result.Snapshot.Ports!);
        Assert.All(result.Snapshot.Services!, s => Assert.False(string.IsNullOrEmpty(s.Name)));
    }
}
