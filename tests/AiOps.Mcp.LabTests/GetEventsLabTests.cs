using AiOps.Mcp.Remoting;
using AiOps.Mcp.Tools.GetEvents;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AiOps.Mcp.LabTests;

/// <summary>
/// Integration tests against a real lab server with the AIOpsReader JEA endpoint.
/// Set AIOPS_LAB_SERVER to the target FQDN to enable; skipped otherwise. Not run in CI.
/// </summary>
public class GetEventsLabTests
{
    private static string? LabServer => Environment.GetEnvironmentVariable("AIOPS_LAB_SERVER");

    [SkippableFact]
    public async Task Reads_system_events_via_jea_endpoint()
    {
        Skip.If(string.IsNullOrEmpty(LabServer), "AIOPS_LAB_SERVER not set");

        var options = Options.Create(new RemotingOptions());
        using var pool = new PsSessionPool(new WsmanRunspaceConnector(options), options, NullLogger<PsSessionPool>.Instance);
        var executor = new JeaRemoteExecutor(pool, options, NullLogger<JeaRemoteExecutor>.Instance);

        var result = await GetEventsTool.GetEvents(
            executor, NullLoggerFactory.Instance,
            [LabServer!], "System",
            DateTimeOffset.UtcNow.AddHours(-24), DateTimeOffset.UtcNow,
            max_events: 10);

        var server = Assert.Single(result.Results);
        Assert.Equal("ok", server.Status);
        Assert.All(server.Events, e => Assert.True(e.Message is null || e.Message.Length <= 500));
    }
}
