using System.Text.Json;
using AiOps.Mcp.Remoting;
using AiOps.Mcp.Tools.RunReadonly;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AiOps.Mcp.LabTests;

/// <summary>
/// Integration tests against a real lab server with the AIOpsReader JEA endpoint.
/// Set AIOPS_LAB_SERVER to the target FQDN to enable; skipped otherwise. Not run in CI.
/// </summary>
public class RunReadonlyLabTests
{
    private static string? LabServer => Environment.GetEnvironmentVariable("AIOPS_LAB_SERVER");

    private static (PsSessionPool Pool, JeaRemoteExecutor Executor) CreateExecutor()
    {
        var options = Options.Create(new RemotingOptions());
        var pool = new PsSessionPool(new WsmanRunspaceConnector(options), options, NullLogger<PsSessionPool>.Instance);
        return (pool, new JeaRemoteExecutor(pool, options, NullLogger<JeaRemoteExecutor>.Instance));
    }

    [SkippableFact]
    public async Task Allowed_cmdlet_runs_via_jea()
    {
        Skip.If(string.IsNullOrEmpty(LabServer), "AIOPS_LAB_SERVER not set");
        var (pool, executor) = CreateExecutor();
        using var _ = pool;

        var result = await RunReadonlyTool.RunReadonly(
            executor, NullLoggerFactory.Instance, LabServer!, FleetCommands.GetService,
            JsonSerializer.Deserialize<Dictionary<string, JsonElement>>("""{"Name":"WinRM"}"""));

        Assert.Equal("ok", result.Status);
        var row = Assert.Single(result.Rows);
        Assert.Equal("WinRM", row["Name"]);
    }

    [SkippableFact]
    public async Task Write_cmdlet_is_rejected_by_jea_not_by_us()
    {
        Skip.If(string.IsNullOrEmpty(LabServer), "AIOPS_LAB_SERVER not set");
        var (pool, executor) = CreateExecutor();
        using var _ = pool;

        // The command goes through verbatim; the JEA endpoint must refuse it because
        // Set-Service is not in the AIOpsReader role capability.
        var result = await RunReadonlyTool.RunReadonly(
            executor, NullLoggerFactory.Instance, LabServer!, "Set-Service",
            JsonSerializer.Deserialize<Dictionary<string, JsonElement>>("""{"Name":"WinRM","StartupType":"Manual"}"""));

        Assert.Equal("error", result.Status);
        Assert.Contains("CommandNotFound", result.ErrorId ?? "");
    }
}
