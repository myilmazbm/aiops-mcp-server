using System.Collections.Concurrent;
using System.Management.Automation.Runspaces;
using System.Text.Json;
using AiOps.Mcp.Remoting;
using AiOps.Mcp.Tools.RunReadonly;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AiOps.Mcp.Tests;

/// <summary>
/// run_readonly through the real executor and a real (local) runspace: exercises
/// parameter binding, pipeline output shaping and error propagation end to end.
/// </summary>
public sealed class RunReadonlyEndToEndTests : IDisposable
{
    private sealed class LocalRunspaceConnector : IRunspaceConnector
    {
        public ConcurrentBag<Runspace> Created { get; } = [];

        public Runspace Connect(string server, string endpointName)
        {
            var runspace = RunspaceFactory.CreateRunspace();
            runspace.Open();
            Created.Add(runspace);
            return runspace;
        }
    }

    private readonly LocalRunspaceConnector _connector = new();
    private readonly PsSessionPool _pool;
    private readonly JeaRemoteExecutor _executor;

    public RunReadonlyEndToEndTests()
    {
        var options = Options.Create(new RemotingOptions());
        _pool = new PsSessionPool(_connector, options, NullLogger<PsSessionPool>.Instance);
        _executor = new JeaRemoteExecutor(_pool, options, NullLogger<JeaRemoteExecutor>.Instance);
    }

    public void Dispose()
    {
        _pool.Dispose();
        foreach (var runspace in _connector.Created)
            runspace.Dispose();
    }

    private Task<AiOps.Contracts.RunReadonlyResult> Call(string command, string? parametersJson = null)
        => RunReadonlyTool.RunReadonly(
            _executor, NullLoggerFactory.Instance, "local1", command,
            parametersJson is null ? null : JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(parametersJson));

    [Fact]
    public async Task Runs_a_cmdlet_with_bound_parameters()
    {
        var result = await Call("Get-Item", """{"Path":"C:\\"}""");

        Assert.Equal("ok", result.Status);
        var row = Assert.Single(result.Rows);
        Assert.Equal(@"C:\", row["FullName"]);
    }

    [Fact]
    public async Task Primitive_pipeline_output_round_trips()
    {
        var result = await Call("Get-Random", """{"Minimum":5,"Maximum":6}""");

        Assert.Equal("ok", result.Status);
        Assert.Equal("5", Assert.Single(result.Rows)["value"]);
    }

    [Fact]
    public async Task Real_error_carries_the_fully_qualified_error_id()
    {
        var result = await Call("Get-Item", """{"Path":"C:\\aiops-missing-xyz","ErrorAction":"Stop"}""");

        Assert.Equal("error", result.Status);
        Assert.Contains("PathNotFound", result.ErrorId);
    }

    [Fact]
    public async Task Array_parameters_bind_to_string_arrays()
    {
        var result = await Call("Get-Item", """{"Path":["C:\\","C:\\Windows"]}""");

        Assert.Equal("ok", result.Status);
        Assert.Equal(2, result.Rows.Count);
    }
}
