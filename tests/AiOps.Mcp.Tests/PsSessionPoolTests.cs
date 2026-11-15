using System.Collections.Concurrent;
using System.Management.Automation.Runspaces;
using AiOps.Mcp.Remoting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AiOps.Mcp.Tests;

/// <summary>
/// Pool tests run against real (local, in-process) runspaces via a fake connector,
/// so session lifecycle and concurrency behavior are exercised for real.
/// </summary>
public sealed class PsSessionPoolTests : IDisposable
{
    private sealed class LocalRunspaceConnector(TimeSpan? connectDelay = null) : IRunspaceConnector
    {
        public ConcurrentDictionary<string, int> ConnectCounts { get; } = new(StringComparer.OrdinalIgnoreCase);
        public ConcurrentBag<Runspace> Created { get; } = [];

        public Runspace Connect(string server, string endpointName)
        {
            if (connectDelay is { } d)
                Thread.Sleep(d);
            ConnectCounts.AddOrUpdate(server, 1, (_, n) => n + 1);
            var runspace = RunspaceFactory.CreateRunspace();
            runspace.Open();
            Created.Add(runspace);
            return runspace;
        }
    }

    private readonly LocalRunspaceConnector _connector = new();

    private static readonly TimeSpan NoTimeout = Timeout.InfiniteTimeSpan;

    private PsSessionPool CreatePool(int maxSessions = 100, LocalRunspaceConnector? connector = null, int openTimeoutSeconds = 15)
        => new(
            connector ?? _connector,
            Options.Create(new RemotingOptions { MaxSessions = maxSessions, OpenTimeoutSeconds = openTimeoutSeconds }),
            NullLogger<PsSessionPool>.Instance);

    // Convenience overloads: most tests don't care about the workToken or a timeout.
    private static Task<T> Use<T>(PsSessionPool pool, string server, Func<Runspace, T> body)
        => pool.UseSessionAsync(server, (rs, _) => Task.FromResult(body(rs)), NoTimeout, CancellationToken.None);

    public void Dispose()
    {
        foreach (var runspace in _connector.Created)
            runspace.Dispose();
    }

    [Fact]
    public async Task Reuses_the_same_session_for_sequential_calls()
    {
        using var pool = CreatePool();

        var first = await Use(pool, "srv1", rs => rs);
        var second = await Use(pool, "srv1", rs => rs);

        Assert.Same(first, second);
        Assert.Equal(1, _connector.ConnectCounts["srv1"]);
    }

    [Fact]
    public async Task Concurrent_calls_to_one_server_serialize_on_a_single_session()
    {
        using var pool = CreatePool();
        var concurrent = 0;
        var maxConcurrent = 0;

        await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => pool.UseSessionAsync("srv1", async (_, _) =>
        {
            var now = Interlocked.Increment(ref concurrent);
            InterlockedMax(ref maxConcurrent, now);
            await Task.Delay(10);
            Interlocked.Decrement(ref concurrent);
            return 0;
        }, NoTimeout, CancellationToken.None)));

        Assert.Equal(1, maxConcurrent);
        Assert.Equal(1, _connector.ConnectCounts["srv1"]);
    }

    [Fact]
    public async Task Different_endpoints_on_the_same_server_get_separate_sessions()
    {
        // The SCOM reader role lives beside the fleet role on the management server; a
        // session opened against one JEA endpoint must never be reused for the other.
        using var pool = CreatePool();

        var fleet = await pool.UseSessionAsync("scom01", "AIOpsReader", (rs, _) => Task.FromResult(rs), NoTimeout, CancellationToken.None);
        var scom = await pool.UseSessionAsync("scom01", "AIOpsScomReader", (rs, _) => Task.FromResult(rs), NoTimeout, CancellationToken.None);
        var fleetAgain = await pool.UseSessionAsync("scom01", "AIOpsReader", (rs, _) => Task.FromResult(rs), NoTimeout, CancellationToken.None);

        Assert.NotSame(fleet, scom);
        Assert.Same(fleet, fleetAgain);
        Assert.Equal(2, _connector.ConnectCounts["scom01"]);
    }

    [Fact]
    public async Task Broken_session_is_transparently_reopened()
    {
        using var pool = CreatePool();

        var first = await Use(pool, "srv1", rs => rs);
        first.Close(); // simulate the remote side dropping the connection

        var second = await Use(pool, "srv1", rs => rs);

        Assert.NotSame(first, second);
        Assert.Equal(RunspaceState.Opened, second.RunspaceStateInfo.State);
        Assert.Equal(2, _connector.ConnectCounts["srv1"]);
    }

    [Fact]
    public async Task Timeout_marks_the_session_unusable()
    {
        using var pool = CreatePool();

        await Assert.ThrowsAsync<TimeoutException>(() => pool.UseSessionAsync<int>(
            "srv1", (_, _) => throw new TimeoutException("simulated"), NoTimeout, CancellationToken.None));

        await Use(pool, "srv1", _ => 0);

        Assert.Equal(2, _connector.ConnectCounts["srv1"]);
    }

    [Fact]
    public async Task Cancellation_marks_the_session_unusable()
    {
        using var pool = CreatePool();

        // A cancelled pipeline leaves the runspace in an unknown state; it must be dropped.
        await Assert.ThrowsAsync<OperationCanceledException>(() => pool.UseSessionAsync<int>(
            "srv1", (_, _) => throw new OperationCanceledException(), NoTimeout, CancellationToken.None));

        await Use(pool, "srv1", _ => 0);

        Assert.Equal(2, _connector.ConnectCounts["srv1"]);
    }

    [Fact]
    public async Task Non_timeout_failure_keeps_a_healthy_session()
    {
        using var pool = CreatePool();

        await Assert.ThrowsAsync<InvalidOperationException>(() => pool.UseSessionAsync<int>(
            "srv1", (_, _) => throw new InvalidOperationException("command failed"), NoTimeout, CancellationToken.None));

        await Use(pool, "srv1", _ => 0);

        Assert.Equal(1, _connector.ConnectCounts["srv1"]);
    }

    [Fact]
    public async Task Work_timeout_starts_only_after_the_session_is_acquired()
    {
        using var pool = CreatePool();
        var firstEntered = new TaskCompletionSource();
        var release = new TaskCompletionSource();

        // Hold the single per-server session busy for longer than the work timeout.
        var holder = pool.UseSessionAsync("srv1", async (_, _) =>
        {
            firstEntered.SetResult();
            await release.Task;
            return 0;
        }, NoTimeout, CancellationToken.None);

        await firstEntered.Task;

        // This call queues behind the holder for ~300 ms with a 100 ms work timeout. If the
        // timeout counted queue time it would fire; it must not, because the budget starts
        // only once the session is held.
        var queued = pool.UseSessionAsync("srv1", (_, _) => Task.FromResult(42),
            TimeSpan.FromMilliseconds(100), CancellationToken.None);

        await Task.Delay(300);
        release.SetResult();

        Assert.Equal(42, await queued); // did NOT time out despite queuing past the budget
        await holder;
    }

    [Fact]
    public async Task Hung_connect_is_bounded_by_the_open_timeout()
    {
        var slowConnector = new LocalRunspaceConnector(connectDelay: TimeSpan.FromSeconds(10));
        using var pool = CreatePool(connector: slowConnector, openTimeoutSeconds: 1);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pool.UseSessionAsync(
            "srv1", (_, _) => Task.FromResult(0), Timeout.InfiniteTimeSpan, CancellationToken.None));
        sw.Stop();

        // The blocking Connect is orphaned rather than awaited to completion.
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3), $"took {sw.Elapsed}");
        foreach (var rs in slowConnector.Created)
            rs.Dispose();
    }

    [Fact]
    public async Task Connect_time_does_not_consume_the_work_budget()
    {
        // Cold JEA open takes seconds; if the work timeout were armed before connecting, a
        // short work budget would fire before the work even started.
        var slowConnector = new LocalRunspaceConnector(connectDelay: TimeSpan.FromMilliseconds(700));
        using var pool = CreatePool(connector: slowConnector);

        var result = await pool.UseSessionAsync(
            "srv1", (_, _) => Task.FromResult(42), TimeSpan.FromMilliseconds(300), CancellationToken.None);

        Assert.Equal(42, result);
        foreach (var rs in slowConnector.Created)
            rs.Dispose();
    }

    [Fact]
    public async Task New_server_does_not_spin_when_every_other_slot_is_busy()
    {
        // At the cap with every slot mid-pipeline, the fresh slot would be the only evictable
        // one: evict → continue → re-create → evict… Excluding it lets the call proceed.
        using var pool = CreatePool(maxSessions: 2);
        var release = new TaskCompletionSource();
        var entered = 0;
        var holders = new[] { "srv1", "srv2" }.Select(s => pool.UseSessionAsync(s, async (_, _) =>
        {
            Interlocked.Increment(ref entered);
            await release.Task;
            return 0;
        }, NoTimeout, CancellationToken.None)).ToList();
        while (Volatile.Read(ref entered) < 2)
            await Task.Delay(10);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var third = await pool.UseSessionAsync("srv3", (_, _) => Task.FromResult(3), NoTimeout, cts.Token);

        Assert.Equal(3, third); // completed instead of spinning until a holder finished
        release.SetResult();
        await Task.WhenAll(holders);
    }

    [Fact]
    public async Task Evicts_least_recently_used_session_beyond_the_cap()
    {
        using var pool = CreatePool(maxSessions: 2);

        var evicted = await Use(pool, "srv1", rs => rs);
        await Use(pool, "srv2", _ => 0);
        await Use(pool, "srv3", _ => 0); // evicts srv1

        await Use(pool, "srv1", _ => 0); // must reconnect

        Assert.Equal(2, _connector.ConnectCounts["srv1"]);
        Assert.Equal(1, _connector.ConnectCounts["srv3"]);
        // The evicted runspace must actually be closed, not merely forgotten (WinRM quota leak).
        // Eviction closes it off the pool gate, on a background task, so wait for it: asserting
        // immediately raced that task and failed on a slower CI runner.
        await WaitUntil(() => evicted.RunspaceStateInfo.State != RunspaceState.Opened);
        Assert.Equal(RunspaceState.Closed, evicted.RunspaceStateInfo.State);
    }

    /// <summary>Polls for a condition a background task will make true; the assert that follows reports a timeout.</summary>
    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(20);
    }

    [Fact]
    public async Task Survives_eviction_races_under_heavy_concurrency_without_orphaning_sessions()
    {
        using var pool = CreatePool(maxSessions: 3);
        var servers = Enumerable.Range(0, 8).Select(i => $"srv{i}").ToArray();

        var calls = Enumerable.Range(0, 80).Select(i => pool.UseSessionAsync(
            servers[i % servers.Length],
            async (rs, _) =>
            {
                await Task.Delay(2);
                return rs.RunspaceStateInfo.State;
            },
            NoTimeout,
            CancellationToken.None));

        var states = await Task.WhenAll(calls);
        Assert.All(states, s => Assert.Equal(RunspaceState.Opened, s));

        // After disposing, no runspace the pool ever created may still be open.
        // Sessions evicted during the run are closed on background tasks; give those a moment.
        pool.Dispose();
        await WaitUntil(() => _connector.Created.All(rs => rs.RunspaceStateInfo.State != RunspaceState.Opened));
        Assert.DoesNotContain(_connector.Created, rs => rs.RunspaceStateInfo.State == RunspaceState.Opened);
    }

    [Fact]
    public async Task Dispose_does_not_yank_a_runspace_from_an_in_flight_call()
    {
        var pool = CreatePool();
        var entered = new TaskCompletionSource();
        var release = new TaskCompletionSource();

        var inFlight = pool.UseSessionAsync("srv1", async (rs, _) =>
        {
            entered.SetResult();
            await release.Task;
            // The worker must still see a live runspace, not one disposed under it.
            return rs.RunspaceStateInfo.State;
        }, NoTimeout, CancellationToken.None);

        await entered.Task;
        var disposeTask = Task.Run(pool.Dispose); // blocks up to the grace period on the busy slot
        release.SetResult();

        var state = await inFlight; // must NOT throw NullReferenceException
        Assert.Equal(RunspaceState.Opened, state);
        await disposeTask;
    }

    [Fact]
    public async Task Dispose_rejects_further_use()
    {
        var pool = CreatePool();
        await Use(pool, "srv1", _ => 0);
        pool.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => Use(pool, "srv1", _ => 0));
    }

    [Fact]
    public async Task Misconfigured_zero_max_sessions_does_not_livelock()
    {
        using var pool = new PsSessionPool(
            _connector,
            Options.Create(new RemotingOptions { MaxSessions = 0 }),
            NullLogger<PsSessionPool>.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        // Clamped to 1: the call completes instead of spinning forever.
        var result = await pool.UseSessionAsync("srv1", (_, _) => Task.FromResult(7), NoTimeout, cts.Token);
        Assert.Equal(7, result);
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while (value > (current = Volatile.Read(ref target)))
        {
            if (Interlocked.CompareExchange(ref target, value, current) == current)
                break;
        }
    }

    [Fact]
    public async Task The_pool_returns_to_the_cap_once_busy_slots_are_released()
    {
        // Trimming used to run only when a NEW key was created. A trim that found every
        // candidate busy was therefore never retried once the fleet had been swept once, and
        // the pool stayed permanently above MaxSessions, holding WinRM shells open on servers
        // nobody had queried in days.
        using var pool = CreatePool(maxSessions: 2);
        using var hold = new SemaphoreSlim(0, 2);

        var busy1 = pool.UseSessionAsync("srv1", async (_, _) => { await hold.WaitAsync(); return 0; }, NoTimeout, CancellationToken.None);
        var busy2 = pool.UseSessionAsync("srv2", async (_, _) => { await hold.WaitAsync(); return 0; }, NoTimeout, CancellationToken.None);
        while (_connector.ConnectCounts.Count < 2)
            await Task.Delay(10);

        // Third slot cannot evict anything: both candidates are mid-pipeline.
        await Use(pool, "srv3", rs => rs);

        hold.Release(2);
        await Task.WhenAll(busy1, busy2);

        // Re-acquiring an EXISTING key must now trim; srv1 is the least recently used.
        await Use(pool, "srv3", rs => rs);
        await Use(pool, "srv1", rs => rs);

        Assert.Equal(2, _connector.ConnectCounts["srv1"]);
    }

    [Fact]
    public async Task An_evicted_session_is_still_closed_although_eviction_no_longer_blocks_the_caller()
    {
        // Closing a remote runspace is a WSMan round trip that can block for the full
        // operation timeout, so it was moved off the pool gate and off the caller's thread.
        // It must still actually happen, or every eviction would leak a shell on the target.
        using var pool = CreatePool(maxSessions: 1);

        var first = await Use(pool, "srv1", rs => rs);
        await Use(pool, "srv2", rs => rs);

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (first.RunspaceStateInfo.State == RunspaceState.Opened && DateTime.UtcNow < deadline)
            await Task.Delay(20);

        Assert.NotEqual(RunspaceState.Opened, first.RunspaceStateInfo.State);
    }
}
