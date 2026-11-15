using System.Threading.Channels;
using AiOps.Contracts;
using Microsoft.Extensions.Options;

namespace AiOps.Mcp.Alerts;

/// <summary>
/// Bounded in-memory alert queue between the webhook endpoint and the forwarder.
/// On overflow the oldest alert is dropped (and logged) — losing an old alert beats
/// blocking SCOM, and nothing is ever persisted centrally by design.
/// </summary>
public sealed class AlertQueue
{
    private readonly Channel<ScomAlert> _channel;
    private long _dropped;

    public AlertQueue(IOptions<AlertsOptions> options, ILogger<AlertQueue> logger)
    {
        _channel = Channel.CreateBounded<ScomAlert>(
            new BoundedChannelOptions(Math.Max(1, options.Value.QueueCapacity))
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
            },
            dropped =>
            {
                Interlocked.Increment(ref _dropped);
                logger.LogWarning("Alert queue full; dropped oldest alert {AlertId} ({Name})", dropped.AlertId, dropped.Name);
            });
    }

    public long DroppedCount => Interlocked.Read(ref _dropped);

    public void Enqueue(ScomAlert alert)
    {
        // DropOldest guarantees TryWrite succeeds unless the channel is completed (never is).
        _channel.Writer.TryWrite(alert);
    }

    public IAsyncEnumerable<ScomAlert> ReadAllAsync(CancellationToken cancellationToken)
        => _channel.Reader.ReadAllAsync(cancellationToken);
}
