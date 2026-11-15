namespace AiOps.Mcp.Remoting;

/// <summary>Bounded-concurrency fan-out over a server list; result order matches input order.</summary>
public static class FanOut
{
    public static async Task<IReadOnlyList<T>> RunAsync<T>(
        IReadOnlyList<string> servers,
        int maxConcurrency,
        Func<string, Task<T>> body,
        CancellationToken cancellationToken)
    {
        using var throttle = new SemaphoreSlim(maxConcurrency);
        var tasks = servers.Select(async server =>
        {
            await throttle.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await body(server).ConfigureAwait(false);
            }
            finally
            {
                throttle.Release();
            }
        }).ToList();

        return await Task.WhenAll(tasks).ConfigureAwait(false);
    }
}
