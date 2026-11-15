using System.Management.Automation;
using AiOps.Mcp.Remoting;
using AiOps.Mcp.Tools.GetCounters;
using NSubstitute;

namespace AiOps.Mcp.Tests;

public class CounterNameTableCacheTests
{
    private sealed class FakeRegistryValue
    {
        public string[] Counter { get; init; } = ["238", "Processor"];
    }

    [Fact]
    public async Task One_callers_cancellation_does_not_fault_the_other_waiter()
    {
        // The shared single-flight fetch must not bake caller A's token in: A cancelling
        // would otherwise fault B's identical, uncancelled request.
        var executor = Substitute.For<IRemoteExecutor>();
        var gate = new TaskCompletionSource<ServerCallResult>();
        executor.InvokeAsync("srv1", Arg.Any<RemoteCommand>(), Arg.Any<CancellationToken>())
            .Returns(_ => gate.Task);
        var cache = new CounterNameTableCache();

        using var ctsA = new CancellationTokenSource();
        var a = cache.GetAsync("srv1", executor, ctsA.Token);
        var b = cache.GetAsync("srv1", executor, CancellationToken.None);

        ctsA.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => a);

        gate.SetResult(new ServerCallResult
        {
            Server = "srv1",
            Status = ServerCallStatus.Ok,
            Output = [PSObject.AsPSObject(new FakeRegistryValue())],
        });

        var result = await b; // B still gets the table
        Assert.NotNull(result.Table);
        Assert.Equal("Processor", result.Table![238]);
        await executor.Received(1).InvokeAsync("srv1", Arg.Any<RemoteCommand>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Concurrent_cold_lookups_make_a_single_remote_call()
    {
        var executor = Substitute.For<IRemoteExecutor>();
        var gate = new TaskCompletionSource<ServerCallResult>();
        executor.InvokeAsync("srv1", Arg.Any<RemoteCommand>(), Arg.Any<CancellationToken>())
            .Returns(_ => gate.Task);
        var cache = new CounterNameTableCache();

        var first = cache.GetAsync("srv1", executor, CancellationToken.None);
        var second = cache.GetAsync("srv1", executor, CancellationToken.None);
        gate.SetResult(new ServerCallResult
        {
            Server = "srv1",
            Status = ServerCallStatus.Ok,
            Output = [PSObject.AsPSObject(new FakeRegistryValue())],
        });
        await Task.WhenAll(first, second);

        await executor.Received(1).InvokeAsync("srv1", Arg.Any<RemoteCommand>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Failed_fetch_is_not_cached()
    {
        var executor = Substitute.For<IRemoteExecutor>();
        executor.InvokeAsync("srv1", Arg.Any<RemoteCommand>(), Arg.Any<CancellationToken>())
            .Returns(new ServerCallResult { Server = "srv1", Status = ServerCallStatus.Error, Error = "denied" });
        var cache = new CounterNameTableCache();

        var one = await cache.GetAsync("srv1", executor, CancellationToken.None);
        var two = await cache.GetAsync("srv1", executor, CancellationToken.None);

        Assert.Null(one.Table);
        Assert.Null(two.Table);
        await executor.Received(2).InvokeAsync("srv1", Arg.Any<RemoteCommand>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task The_cache_is_bounded_so_a_fleet_wide_sweep_does_not_grow_it_forever()
    {
        // Each entry is a parsed Perflib table of several thousand index/name pairs and the
        // cache lives for the life of the process, so an unbounded one turned a single sweep
        // across the fleet into a few hundred megabytes that were never released.
        var cache = new CounterNameTableCache();
        var executor = Substitute.For<IRemoteExecutor>();
        executor.InvokeAsync(Arg.Any<string>(), Arg.Any<RemoteCommand>(), Arg.Any<CancellationToken>())
            .Returns(call => new ServerCallResult
            {
                Server = call.ArgAt<string>(0),
                Status = ServerCallStatus.Ok,
                Output = [PSObject.AsPSObject(new { Counter = new[] { "1", "Processor", "6", "% Processor Time" } })],
            });

        var total = CounterNameTableCache.MaxCachedServers + 25;
        for (var i = 0; i < total; i++)
            Assert.NotNull((await cache.GetAsync($"srv{i:D4}", executor, CancellationToken.None)).Table);

        // The most recent server must still be cached (no remote call on a second read) while
        // the oldest has been evicted (a second read refetches).
        await cache.GetAsync($"srv{total - 1:D4}", executor, CancellationToken.None);
        await cache.GetAsync("srv0000", executor, CancellationToken.None);

        await executor.Received(total + 1).InvokeAsync(Arg.Any<string>(), Arg.Any<RemoteCommand>(), Arg.Any<CancellationToken>());
    }
}
