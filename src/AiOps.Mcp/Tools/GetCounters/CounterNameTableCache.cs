using System.Collections.Concurrent;
using AiOps.Mcp.Remoting;

namespace AiOps.Mcp.Tools.GetCounters;

/// <summary>
/// Per-server cache of the Perflib CurrentLanguage index→name table used for
/// counter path localization. Only successful fetches are cached; a server's
/// locale table does not change, so entries never expire. Fetches are single-flighted
/// per server so concurrent cold lookups don't each make a redundant remote round trip
/// (which would serialize behind the single pooled session and burn each caller's budget).
/// </summary>
public sealed class CounterNameTableCache
{
    private const string PerflibKey = @"HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Perflib\CurrentLanguage";

    /// <summary>Upper bound on cached locale tables; beyond it the least recently used are dropped.</summary>
    public const int MaxCachedServers = 200;

    private readonly ConcurrentDictionary<string, IReadOnlyDictionary<int, string>> _tables = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, long> _lastUsed = new(StringComparer.OrdinalIgnoreCase);
    private long _clock;
    private readonly ConcurrentDictionary<string, Lazy<Task<FetchResult>>> _inFlight = new(StringComparer.OrdinalIgnoreCase);

    public sealed record FetchResult(IReadOnlyDictionary<int, string>? Table, ServerCallResult? FailedCall);

    public async Task<FetchResult> GetAsync(string server, IRemoteExecutor executor, CancellationToken cancellationToken)
    {
        if (_tables.TryGetValue(server, out var cached))
        {
            _lastUsed[server] = Interlocked.Increment(ref _clock);
            return new FetchResult(cached, null);
        }

        // One fetch per server at a time; concurrent callers await the same task. The shared
        // fetch runs on CancellationToken.None — baking the FIRST caller's token in would let
        // one request's cancellation fault every other waiter — and is bounded instead by the
        // executor's per-server timeout. Each caller observes its own token via WaitAsync.
        var lazy = _inFlight.GetOrAdd(server,
            key => new Lazy<Task<FetchResult>>(() => FetchAsync(key, executor)));
        try
        {
            return await lazy.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // Drop the in-flight entry so a failed fetch is not pinned (success is served
            // from _tables next time regardless).
            _inFlight.TryRemove(new KeyValuePair<string, Lazy<Task<FetchResult>>>(server, lazy));
        }
    }

    private async Task<FetchResult> FetchAsync(string server, IRemoteExecutor executor)
    {
        if (_tables.TryGetValue(server, out var cached))
            return new FetchResult(cached, null);

        var call = await executor.InvokeAsync(
            server,
            RemoteCommand.Create(FleetCommands.GetItemProperty, ("Path", PerflibKey), ("Name", "Counter")),
            CancellationToken.None).ConfigureAwait(false);

        if (call.Status != ServerCallStatus.Ok || call.Output.Count == 0)
            return new FetchResult(null, call);

        var multiSz = PsValue.Get<string[]>(call.Output[0], "Counter");
        if (multiSz is not { Length: > 1 })
            return new FetchResult(null, call with { Status = ServerCallStatus.Error, Error = "Perflib counter table is empty or unreadable" });

        var table = CounterCatalog.ParsePerflibTable(multiSz);
        _tables[server] = table;
        _lastUsed[server] = Interlocked.Increment(ref _clock);
        TrimToCap();
        return new FetchResult(table, null);
    }

    /// <summary>
    /// Bounds the cache. Each entry is a parsed Perflib table — several thousand index/name
    /// pairs — and this object lives for the life of the process, so one fleet-wide counter
    /// query across ~500 servers used to add hundreds of megabytes that were never released.
    /// A locale table does not change, so plain LRU eviction costs only a re-fetch.
    /// </summary>
    private void TrimToCap()
    {
        if (_tables.Count <= MaxCachedServers)
            return;

        foreach (var server in _lastUsed.OrderBy(kv => kv.Value)
                     .Take(_tables.Count - MaxCachedServers)
                     .Select(kv => kv.Key)
                     .ToList())
        {
            _tables.TryRemove(server, out _);
            _lastUsed.TryRemove(server, out _);
        }
    }
}
