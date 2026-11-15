using System.Management.Automation.Runspaces;
using Microsoft.Extensions.Options;

namespace AiOps.Mcp.Remoting;

/// <summary>
/// Keeps at most one persistent runspace per server, capped at
/// <see cref="RemotingOptions.MaxSessions"/> total with LRU eviction.
/// A broken or timed-out session is dropped and transparently reopened on the next call.
/// </summary>
public sealed class PsSessionPool(
    IRunspaceConnector connector,
    IOptions<RemotingOptions> options,
    ILogger<PsSessionPool> logger) : IDisposable
{
    private readonly RemotingOptions _options = options.Value;
    // Clamp: a misconfigured MaxSessions <= 0 would make every fresh slot its own eviction
    // victim, livelocking the first call.
    private readonly int _maxSessions = Math.Max(1, options.Value.MaxSessions);
    private readonly Lock _gate = new();
    private readonly Dictionary<string, ServerSlot> _slots = new(StringComparer.OrdinalIgnoreCase);
    private long _clock;
    private bool _disposed;

    private sealed class ServerSlot
    {
        public SemaphoreSlim Mutex { get; } = new(1, 1);
        public Runspace? Runspace;
        public long LastUsed;
        /// <summary>Set under the pool gate while holding the mutex; a waiter seeing it must re-acquire a slot.</summary>
        public bool Evicted;
    }

    /// <summary>
    /// Runs <paramref name="work"/> against the server's pooled session. The per-server
    /// <paramref name="workTimeout"/> is armed only AFTER the session is acquired and opened —
    /// queuing behind another call on the single per-server session does not consume the work
    /// budget, so a healthy-but-busy server is never misreported as timed out.
    /// </summary>
    public Task<T> UseSessionAsync<T>(
        string server,
        Func<Runspace, CancellationToken, Task<T>> work,
        TimeSpan workTimeout,
        CancellationToken cancellationToken)
        => UseSessionAsync(server, _options.JeaEndpointName, work, workTimeout, cancellationToken);

    /// <summary>Same, against a specific JEA endpoint; sessions are pooled per (server, endpoint).</summary>
    public async Task<T> UseSessionAsync<T>(
        string server,
        string endpointName,
        Func<Runspace, CancellationToken, Task<T>> work,
        TimeSpan workTimeout,
        CancellationToken cancellationToken)
    {
        var key = endpointName + "@" + server;
        while (true)
        {
            var slot = GetSlot(key);
            // Queue wait is bounded by the caller token only — never the work timeout.
            await slot.Mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (slot.Evicted)
                    continue; // lost the race with LRU eviction; grab a fresh slot

                // Connect phase has its own budget (OpenTimeoutSeconds): a cold JEA open
                // (WSMan + virtual-account startup, 5-8 s) must not eat into the work budget,
                // or a healthy server sampling for 25 s would be misreported as unreachable.
                if (slot.Runspace is not { RunspaceStateInfo.State: RunspaceState.Opened })
                {
                    CloseInBackground(Detach(slot), server);
                    using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    connectCts.CancelAfter(TimeSpan.FromSeconds(_options.OpenTimeoutSeconds));
                    slot.Runspace = await ConnectAsync(server, endpointName, connectCts.Token).ConfigureAwait(false);
                    logger.LogDebug("Opened session to {Server} ({Endpoint})", server, endpointName);
                }

                // The work timeout is armed only now — after the session is held AND open.
                using var workCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                if (workTimeout > TimeSpan.Zero)
                    workCts.CancelAfter(workTimeout);

                try
                {
                    return await work(slot.Runspace, workCts.Token).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    // A timed-out or cancelled pipeline leaves the runspace in an unknown
                    // state; never reuse it. Null-guard: Dispose may have nulled it concurrently.
                    if (ex is TimeoutException or OperationCanceledException
                        || slot.Runspace is null
                        || slot.Runspace.RunspaceStateInfo.State != RunspaceState.Opened)
                    {
                        // Detach, then close off the caller's thread. Closing a runspace whose
                        // pipeline was just stopped against a wedged host blocks again for the
                        // full OperationTimeout, which would double the per-server budget the
                        // tool advertises and hold the fan-out throttle slot for twice as long.
                        CloseInBackground(Detach(slot), server);
                        logger.LogDebug("Dropped session to {Server} after failure", server);
                    }

                    throw;
                }
            }
            finally
            {
                slot.Mutex.Release();
            }
        }
    }

    /// <summary>
    /// Opens a runspace, cancellable by the budget token. connector.Connect blocks (WSMan
    /// Open), so a hung connect is orphaned via WaitAsync rather than left to blow the budget;
    /// the orphan is disposed if it eventually opens.
    /// </summary>
    private async Task<Runspace> ConnectAsync(string server, string endpointName, CancellationToken cancellationToken)
    {
        var connectTask = Task.Run(() => connector.Connect(server, endpointName), CancellationToken.None);
        try
        {
            return await connectTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _ = connectTask.ContinueWith(
                t =>
                {
                    if (t.IsCompletedSuccessfully)
                    {
                        try { t.Result.Dispose(); } catch { /* best effort */ }
                    }
                    else if (t.Exception is { } failure)
                    {
                        // Observe it. An orphaned connect that faults after the budget expired
                        // would otherwise surface only as an UnobservedTaskException at GC time,
                        // invisible by default and fatal if that policy is ever turned on.
                        logger.LogDebug(failure, "Orphaned connect to {Server} failed after the open budget expired", server);
                    }
                },
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            throw;
        }
    }

    /// <summary>
    /// Closing a remote runspace is a WSMan Delete Shell round trip, bounded only by
    /// OperationTimeout (30 s by default). It must never run under the pool gate — one
    /// power-cycled server would then stall every other server's fan-out for the whole
    /// timeout, synchronously, on thread-pool threads. Eviction hands the runspace here
    /// instead: the mutex is still held for each victim, so nothing else can touch it, and
    /// the caller that triggered the eviction does not pay for it either.
    /// </summary>
    private void DisposeEvictedInBackground(List<(string Key, ServerSlot Slot)> victims)
    {
        if (victims.Count == 0)
            return;

        _ = Task.Run(() =>
        {
            foreach (var (key, slot) in victims)
            {
                try
                {
                    DropRunspace(slot);
                    logger.LogDebug("Closed evicted session for {Server}", key);
                }
                catch (Exception ex)
                {
                    // Nothing upstream can act on a failed close of an already-evicted
                    // session, and an unobserved exception here would be invisible.
                    logger.LogDebug(ex, "Closing evicted session for {Server} failed", key);
                }
                finally
                {
                    slot.Mutex.Release();
                }
            }
        });
    }

    private ServerSlot GetSlot(string server)
    {
        List<(string Key, ServerSlot Slot)> victims;
        ServerSlot result;

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (!_slots.TryGetValue(server, out var slot))
            {
                slot = new ServerSlot();
                _slots[server] = slot;
                slot.LastUsed = ++_clock;
                // The fresh slot is excluded from eviction outright: its caller has not
                // taken the mutex yet, so when every other slot is busy it would be the only
                // evictable one — evicted, re-created, evicted… a hot spin until an unrelated
                // pipeline finishes. Exceeding the cap by one transiently is the lesser evil.
                victims = TrimLocked(exclude: slot);
            }
            else
            {
                slot.LastUsed = ++_clock;
                // Trim on every acquisition, not only when a slot is created. A trim that
                // found every candidate busy used to be retried only on the next NEW key, so
                // once the fleet had been swept once the pool stayed above MaxSessions for
                // the life of the process.
                victims = TrimLocked();
            }

            result = slot;
        }

        DisposeEvictedInBackground(victims);
        return result;
    }

    /// <summary>
    /// Removes LRU slots down to the cap and returns the victims with their mutexes STILL
    /// HELD, for the caller to close outside the gate. Busy slots are never evicted.
    /// </summary>
    private List<(string Key, ServerSlot Slot)> TrimLocked(ServerSlot? exclude = null)
    {
        List<(string Key, ServerSlot Slot)> victims = [];

        while (_slots.Count > _maxSessions)
        {
            ServerSlot? victim = null;
            string? victimKey = null;

            // Oldest slot whose mutex we can take right now; busy slots are never evicted.
            foreach (var (key, slot) in _slots.OrderBy(kv => kv.Value.LastUsed))
            {
                if (ReferenceEquals(slot, exclude))
                    continue;

                if (slot.Mutex.Wait(0))
                {
                    victim = slot;
                    victimKey = key;
                    break;
                }
            }

            if (victim is null)
                break; // everything is busy; a later acquisition will trim

            victim.Evicted = true;
            _slots.Remove(victimKey!);
            victims.Add((victimKey!, victim));
            logger.LogDebug("Evicted pooled session for {Server}", victimKey);
        }

        return victims;
    }

    /// <summary>Takes the runspace off the slot so the slot can be reused while the old one closes.</summary>
    private static Runspace? Detach(ServerSlot slot)
    {
        var runspace = slot.Runspace;
        slot.Runspace = null;
        return runspace;
    }

    private void CloseInBackground(Runspace? runspace, string server)
    {
        if (runspace is null)
            return;

        _ = Task.Run(() =>
        {
            try
            {
                runspace.Dispose();
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Closing session to {Server} failed", server);
            }
        });
    }

    private static void DropRunspace(ServerSlot slot)
    {
        try
        {
            slot.Runspace?.Dispose();
        }
        catch
        {
            // best-effort cleanup of an already-broken session
        }

        slot.Runspace = null;
    }

    public void Dispose()
    {
        List<ServerSlot> slots;
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            slots = [.. _slots.Values];
            _slots.Clear();
        }

        // Take each session before disposing it, so a runspace is never yanked out from
        // under an in-flight pipeline. A slot still busy after the grace period is marked
        // Evicted and left for its worker to finish; the process is shutting down anyway.
        var abandoned = 0;
        foreach (var slot in slots)
        {
            var acquired = slot.Mutex.Wait(TimeSpan.FromSeconds(2));
            slot.Evicted = true;
            if (acquired)
            {
                DropRunspace(slot);
                slot.Mutex.Release();
            }
            else
            {
                abandoned++;
            }
        }

        // A slot still busy after the grace period keeps its runspace: yanking it out from
        // under a live pipeline is worse. The shell then survives on the target until WinRM's
        // idle timeout, holding one of its MaxShellsPerUser slots, so say so rather than
        // leaving a rolling restart to fail mysteriously on the next start.
        if (abandoned > 0)
            logger.LogWarning(
                "{Count} pooled session(s) were still in flight at shutdown; their WinRM shells are left " +
                "to the target's idle timeout and continue to occupy a MaxShellsPerUser slot until then.",
                abandoned);
    }
}
