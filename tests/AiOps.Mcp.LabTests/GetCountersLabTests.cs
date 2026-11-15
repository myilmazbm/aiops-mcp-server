using AiOps.Mcp.Remoting;
using AiOps.Mcp.Tools.GetCounters;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AiOps.Mcp.LabTests;

/// <summary>
/// Integration tests against a real lab server with the AIOpsReader JEA endpoint.
/// Set AIOPS_LAB_SERVER to the target FQDN to enable; skipped otherwise. Not run in CI.
/// A Turkish-locale lab server is the interesting case: it exercises PDH index translation.
/// </summary>
public class GetCountersLabTests
{
    private static string? LabServer => Environment.GetEnvironmentVariable("AIOPS_LAB_SERVER");

    [SkippableFact]
    public async Task Samples_cpu_counter_via_jea_endpoint()
    {
        Skip.If(string.IsNullOrEmpty(LabServer), "AIOPS_LAB_SERVER not set");

        var options = Options.Create(new RemotingOptions());
        using var pool = new PsSessionPool(new WsmanRunspaceConnector(options), options, NullLogger<PsSessionPool>.Instance);
        var executor = new JeaRemoteExecutor(pool, options, NullLogger<JeaRemoteExecutor>.Instance);

        var result = await GetCountersTool.GetCounters(
            executor, new CounterNameTableCache(), options, NullLoggerFactory.Instance,
            [LabServer!], [@"\Processor(_Total)\% Processor Time"],
            samples: 3, interval: 1);

        var server = Assert.Single(result.Results);
        Assert.True(server.Status == "ok",
            $"status={server.Status} error={server.Error} counters={server.Counters.Count}");
        var stats = Assert.Single(server.Counters);
        Assert.Equal(3, stats.SampleCount);
        Assert.InRange(stats.Avg, 0, 100);
    }
}
