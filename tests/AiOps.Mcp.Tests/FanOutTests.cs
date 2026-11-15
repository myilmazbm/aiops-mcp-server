using AiOps.Mcp.Remoting;

namespace AiOps.Mcp.Tests;

public class FanOutTests
{
    [Fact]
    public async Task Bounds_concurrency_to_the_configured_maximum()
    {
        // Without the semaphore, all 20 would run at once and blow WinRM per-user quotas.
        var servers = Enumerable.Range(0, 20).Select(i => $"srv{i}").ToArray();
        var concurrent = 0;
        var peak = 0;

        await FanOut.RunAsync(servers, maxConcurrency: 4, async _ =>
        {
            var now = Interlocked.Increment(ref concurrent);
            InterlockedMax(ref peak, now);
            await Task.Delay(15);
            Interlocked.Decrement(ref concurrent);
            return 0;
        }, CancellationToken.None);

        Assert.True(peak <= 4, $"peak concurrency was {peak}");
    }

    [Fact]
    public async Task Preserves_input_order_in_results()
    {
        var servers = new[] { "a", "b", "c", "d" };

        var results = await FanOut.RunAsync(servers, maxConcurrency: 4, async s =>
        {
            // Reverse the completion order relative to input.
            await Task.Delay(s == "a" ? 40 : 5);
            return s.ToUpperInvariant();
        }, CancellationToken.None);

        Assert.Equal(["A", "B", "C", "D"], results);
    }

    [Fact]
    public async Task A_body_that_throws_faults_the_whole_call()
    {
        // Documents the contract: FanOut does NOT swallow exceptions — isolation is the
        // caller's job (JeaRemoteExecutor.InvokeAsync catches and returns Unreachable/Error
        // so InvokeManyAsync never hands FanOut a throwing body).
        await Assert.ThrowsAsync<InvalidOperationException>(() => FanOut.RunAsync(
            new[] { "ok1", "boom", "ok2" }, maxConcurrency: 4,
            s => s == "boom" ? throw new InvalidOperationException() : Task.FromResult(0),
            CancellationToken.None));
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
}
