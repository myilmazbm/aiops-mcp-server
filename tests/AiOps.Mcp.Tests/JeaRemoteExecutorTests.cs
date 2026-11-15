using System.Collections.Concurrent;
using System.Diagnostics;
using System.Management.Automation.Runspaces;
using AiOps.Mcp.Remoting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AiOps.Mcp.Tests;

/// <summary>
/// Executor tests run real commands in local in-process runspaces (the fake connector
/// stands in for WSMan), so pipeline, error-stream, and timeout paths are exercised for real.
/// </summary>
public sealed class JeaRemoteExecutorTests : IDisposable
{
    private sealed class LocalRunspaceConnector : IRunspaceConnector
    {
        public ConcurrentDictionary<string, int> ConnectCounts { get; } = new(StringComparer.OrdinalIgnoreCase);
        public ConcurrentBag<Runspace> Created { get; } = [];
        /// <summary>Servers for which Connect throws a transport failure (a dead host).</summary>
        public HashSet<string> DeadServers { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Runspace Connect(string server, string endpointName)
        {
            if (DeadServers.Contains(server))
                throw new System.Management.Automation.Remoting.PSRemotingTransportException($"cannot connect to {server}");

            ConnectCounts.AddOrUpdate(server, 1, (_, n) => n + 1);
            var runspace = RunspaceFactory.CreateRunspace();
            runspace.Open();
            Created.Add(runspace);
            return runspace;
        }
    }

    private readonly LocalRunspaceConnector _connector = new();
    private readonly PsSessionPool _pool;
    private readonly JeaRemoteExecutor _executor;

    public JeaRemoteExecutorTests()
    {
        var options = Options.Create(new RemotingOptions { PerServerTimeoutSeconds = 3, MaxConcurrency = 4 });
        _pool = new PsSessionPool(_connector, options, NullLogger<PsSessionPool>.Instance);
        _executor = new JeaRemoteExecutor(_pool, options, NullLogger<JeaRemoteExecutor>.Instance);
    }

    public void Dispose()
    {
        _pool.Dispose();
        foreach (var runspace in _connector.Created)
            runspace.Dispose();
    }

    [Fact]
    public async Task Successful_command_returns_output()
    {
        var result = await _executor.InvokeAsync(
            "local1", RemoteCommand.Create("Get-Date"), CancellationToken.None);

        Assert.Equal(ServerCallStatus.Ok, result.Status);
        Assert.Single(result.Output);
        Assert.Null(result.Error);
    }

    [Fact]
    public async Task Terminating_error_yields_error_status_with_error_id()
    {
        var result = await _executor.InvokeAsync(
            "local1",
            RemoteCommand.Create("Get-Item", ("Path", @"C:\aiops-does-not-exist-42"), ("ErrorAction", "Stop")),
            CancellationToken.None);

        Assert.Equal(ServerCallStatus.Error, result.Status);
        Assert.Contains("PathNotFound", result.ErrorId);
    }

    [Fact]
    public async Task Non_terminating_error_yields_error_status_with_partial_output()
    {
        var result = await _executor.InvokeAsync(
            "local1",
            RemoteCommand.Create("Get-Item", ("Path", new[] { @"C:\", @"C:\aiops-does-not-exist-42" })),
            CancellationToken.None);

        Assert.Equal(ServerCallStatus.Error, result.Status);
        Assert.Contains("PathNotFound", result.ErrorId);
        Assert.Single(result.Output); // C:\ still came back
    }

    [Fact]
    public async Task Hung_command_times_out_as_unreachable_and_drops_the_session()
    {
        var stopwatch = Stopwatch.StartNew();
        var result = await _executor.InvokeAsync(
            "local1", RemoteCommand.Create("Start-Sleep", ("Seconds", 30)), CancellationToken.None);
        stopwatch.Stop();

        Assert.Equal(ServerCallStatus.Unreachable, result.Status);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(15), $"took {stopwatch.Elapsed}");

        // The timed-out session must not be reused.
        await _executor.InvokeAsync("local1", RemoteCommand.Create("Get-Date"), CancellationToken.None);
        Assert.Equal(2, _connector.ConnectCounts["local1"]);
    }

    [Fact]
    public async Task Fan_out_preserves_input_order_and_isolates_failures()
    {
        var servers = new[] { "local1", "local2", "local3" };

        var results = await _executor.InvokeManyAsync(
            servers,
            RemoteCommand.Create("Get-Date"),
            CancellationToken.None);

        Assert.Equal(servers, results.Select(r => r.Server));
        Assert.All(results, r => Assert.Equal(ServerCallStatus.Ok, r.Status));
    }

    [Fact]
    public async Task Fan_out_isolates_a_dead_server_without_dropping_the_others()
    {
        // A transport failure on one host must yield Unreachable for it and leave the rest
        // Ok — InvokeManyAsync must not fault the whole batch.
        _connector.DeadServers.Add("dead1");
        var servers = new[] { "local1", "dead1", "local2" };

        var results = await _executor.InvokeManyAsync(servers, RemoteCommand.Create("Get-Date"), CancellationToken.None);

        Assert.Equal(servers, results.Select(r => r.Server)); // order preserved
        Assert.Equal(ServerCallStatus.Ok, results[0].Status);
        Assert.Equal(ServerCallStatus.Unreachable, results[1].Status);
        Assert.Equal(ServerCallStatus.Ok, results[2].Status);
    }

    [Fact]
    public async Task Caller_cancellation_propagates_instead_of_masking_as_unreachable()
    {
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(300));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _executor.InvokeAsync(
            "local1", RemoteCommand.Create("Start-Sleep", ("Seconds", 30)), cts.Token));
    }

    [Fact]
    public async Task Piped_commands_run_as_a_single_pipeline()
    {
        var result = await _executor.InvokeAsync(
            "local1",
            RemoteCommand.Create("Get-Item", ("Path", @"C:\"))
                .PipeTo("Select-Object", ("ExpandProperty", "FullName")),
            CancellationToken.None);

        Assert.Equal(ServerCallStatus.Ok, result.Status);
        Assert.Equal(@"C:\", Assert.Single(result.Output).BaseObject);
    }

    [Fact]
    public async Task Null_parameters_are_omitted_from_the_pipeline()
    {
        var result = await _executor.InvokeAsync(
            "local1",
            new RemoteCommand("Get-Date", new Dictionary<string, object?> { ["Date"] = null }),
            CancellationToken.None);

        Assert.Equal(ServerCallStatus.Ok, result.Status);
    }
}
