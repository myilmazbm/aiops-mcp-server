using System.Management.Automation;
using AiOps.Contracts;
using AiOps.Mcp.Inventory;
using AiOps.Mcp.Remoting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace AiOps.Mcp.Tests;

public class NightlyInventoryServiceTests
{
    // --- NextRun scheduling ---

    [Fact]
    public void Before_the_hour_runs_today()
    {
        var now = new DateTimeOffset(2026, 7, 6, 1, 15, 0, TimeSpan.FromHours(3));

        var next = NightlyInventoryService.NextRun(now, 2);

        Assert.Equal(new DateTimeOffset(2026, 7, 6, 2, 0, 0, TimeSpan.FromHours(3)), next);
    }

    [Fact]
    public void After_the_hour_runs_tomorrow()
    {
        var now = new DateTimeOffset(2026, 7, 6, 14, 0, 0, TimeSpan.FromHours(3));

        var next = NightlyInventoryService.NextRun(now, 2);

        Assert.Equal(new DateTimeOffset(2026, 7, 7, 2, 0, 0, TimeSpan.FromHours(3)), next);
    }

    [Fact]
    public void Exactly_at_the_hour_runs_tomorrow_not_now()
    {
        var now = new DateTimeOffset(2026, 7, 6, 2, 0, 0, TimeSpan.FromHours(3));

        var next = NightlyInventoryService.NextRun(now, 2);

        Assert.Equal(now.AddDays(1), next);
    }

    [Fact]
    public void Misconfigured_hour_is_clamped_not_thrown()
    {
        var now = new DateTimeOffset(2026, 7, 6, 12, 0, 0, TimeSpan.Zero);

        var next = NightlyInventoryService.NextRun(now, 99);

        Assert.Equal(23, next.Hour);
        Assert.True(next > now);
    }

    [Fact]
    public void Delay_is_never_negative_at_the_hour_boundary()
    {
        // Exactly at the run hour, NextRun rolls to tomorrow, so the delay is positive; the
        // clamp guarantees Task.Delay never gets a negative argument (which would fault the
        // host past the OCE-only catch).
        var atBoundary = new DateTimeOffset(2026, 7, 6, 2, 0, 0, TimeSpan.Zero);
        Assert.True(NightlyInventoryService.DelayUntilNextRun(atBoundary, 2) >= TimeSpan.Zero);

        var justBefore = new DateTimeOffset(2026, 7, 6, 1, 59, 59, TimeSpan.Zero);
        Assert.InRange(NightlyInventoryService.DelayUntilNextRun(justBefore, 2), TimeSpan.Zero, TimeSpan.FromSeconds(2));
    }

    // --- SweepOnce ---

    private readonly IRemoteExecutor _executor = Substitute.For<IRemoteExecutor>();
    private readonly IInventoryRepository _repository = Substitute.For<IInventoryRepository>();
    private readonly AiOps.Mcp.Inventory.Search.IInventoryIndexer _indexer =
        Substitute.For<AiOps.Mcp.Inventory.Search.IInventoryIndexer>();

    private NightlyInventoryService CreateService(params string[] servers)
    {
        var provider = new ServiceCollection()
            .AddSingleton(_repository)
            .BuildServiceProvider();

        return new NightlyInventoryService(
            new InventoryCollector(_executor),
            provider,
            _indexer,
            Options.Create(new InventoryOptions { SqlConnectionString = "unused-in-test", Servers = servers }),
            Options.Create(new RemotingOptions()),
            NullLogger<NightlyInventoryService>.Instance);
    }

    private void SetupExecutor(Func<string, ServerCallResult> perServer)
        => _executor.InvokeAsync(Arg.Any<string>(), Arg.Any<RemoteCommand>(), Arg.Any<CancellationToken>())
            .Returns(call => perServer(call.ArgAt<string>(0)) with { Server = call.ArgAt<string>(0) });

    [Fact]
    public async Task Sweep_upserts_every_reachable_server()
    {
        SetupExecutor(_ => new ServerCallResult
        {
            Server = "x",
            Status = ServerCallStatus.Ok,
            Output = [PSObject.AsPSObject(new { Name = "WinRM" })],
        });

        await CreateService("srv1", "srv2").SweepOnceAsync(CancellationToken.None);

        await _repository.Received(1).UpsertAsync(
            Arg.Is<InventorySnapshot>(s => s.Server == "srv1" && s.Services != null), Arg.Any<CancellationToken>());
        await _repository.Received(1).UpsertAsync(
            Arg.Is<InventorySnapshot>(s => s.Server == "srv2"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Unreachable_server_is_never_upserted()
    {
        SetupExecutor(server => server == "dead"
            ? new ServerCallResult { Server = server, Status = ServerCallStatus.Unreachable, Error = "timeout" }
            : new ServerCallResult { Server = server, Status = ServerCallStatus.Ok });

        await CreateService("srv1", "dead").SweepOnceAsync(CancellationToken.None);

        await _repository.DidNotReceive().UpsertAsync(
            Arg.Is<InventorySnapshot>(s => s.Server == "dead"), Arg.Any<CancellationToken>());
        await _repository.Received(1).UpsertAsync(
            Arg.Is<InventorySnapshot>(s => s.Server == "srv1"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task One_servers_sql_failure_does_not_abort_the_sweep()
    {
        SetupExecutor(_ => new ServerCallResult { Server = "x", Status = ServerCallStatus.Ok });
        _repository.UpsertAsync(Arg.Is<InventorySnapshot>(s => s.Server == "srv1"), Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new InvalidOperationException("sql down"));

        await CreateService("srv1", "srv2").SweepOnceAsync(CancellationToken.None);

        await _repository.Received(1).UpsertAsync(
            Arg.Is<InventorySnapshot>(s => s.Server == "srv2"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Reachable_servers_are_semantically_indexed()
    {
        SetupExecutor(server => server == "dead"
            ? new ServerCallResult { Server = server, Status = ServerCallStatus.Unreachable, Error = "timeout" }
            : new ServerCallResult { Server = server, Status = ServerCallStatus.Ok });

        await CreateService("srv1", "dead").SweepOnceAsync(CancellationToken.None);

        await _indexer.Received(1).IndexAsync(
            Arg.Is<InventorySnapshot>(s => s.Server == "srv1"), Arg.Any<CancellationToken>());
        await _indexer.DidNotReceive().IndexAsync(
            Arg.Is<InventorySnapshot>(s => s.Server == "dead"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Indexing_failure_is_tolerated_after_the_sql_snapshot_is_saved()
    {
        SetupExecutor(_ => new ServerCallResult { Server = "x", Status = ServerCallStatus.Ok });
        _indexer.IndexAsync(Arg.Any<InventorySnapshot>(), Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new InvalidOperationException("qdrant down"));

        // Must not throw: the vector index is a secondary store.
        await CreateService("srv1", "srv2").SweepOnceAsync(CancellationToken.None);

        await _repository.Received(2).UpsertAsync(Arg.Any<InventorySnapshot>(), Arg.Any<CancellationToken>());
    }
}
