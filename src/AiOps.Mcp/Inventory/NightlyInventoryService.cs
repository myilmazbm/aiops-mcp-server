using AiOps.Mcp.Remoting;
using Microsoft.Extensions.Options;

namespace AiOps.Mcp.Inventory;

/// <summary>
/// The one proactive part of the system: a nightly sweep that refreshes every server's
/// inventory snapshot. Everything else is strictly reactive.
/// </summary>
public sealed class NightlyInventoryService(
    InventoryCollector collector,
    IServiceProvider serviceProvider,
    Search.IInventoryIndexer indexer,
    IOptions<InventoryOptions> inventoryOptions,
    IOptions<RemotingOptions> remotingOptions,
    ILogger<NightlyInventoryService> logger) : BackgroundService
{
    private readonly InventoryOptions _options = inventoryOptions.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (string.IsNullOrEmpty(_options.SqlConnectionString) || _options.Servers.Length == 0)
        {
            logger.LogWarning("Nightly inventory sweep disabled: no SQL connection string or no servers configured");
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = DelayUntilNextRun(DateTimeOffset.Now, _options.NightlyHourLocal);
            logger.LogInformation("Next inventory sweep in {Delay}", delay);

            try
            {
                await Task.Delay(delay, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            try
            {
                await SweepOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // Any failure — a bad sweep, a scheduling edge case — must not kill the host
                // (BackgroundService default is StopHost); the loop simply tries again.
                logger.LogError(ex, "Nightly inventory loop iteration failed");
            }
        }
    }

    /// <summary>Next occurrence of the given local hour, strictly in the future.</summary>
    internal static DateTimeOffset NextRun(DateTimeOffset now, int hourLocal)
    {
        hourLocal = Math.Clamp(hourLocal, 0, 23);
        var todayRun = new DateTimeOffset(now.Year, now.Month, now.Day, hourLocal, 0, 0, now.Offset);
        return todayRun > now ? todayRun : todayRun.AddDays(1);
    }

    /// <summary>
    /// Non-negative delay from <paramref name="now"/> to the next run. Sampling the clock
    /// once (rather than reading DateTimeOffset.Now twice around NextRun) and clamping to
    /// zero prevents a negative Task.Delay argument at the hour boundary — which would throw
    /// ArgumentOutOfRangeException past the OCE-only catch and fault the whole host.
    /// </summary>
    internal static TimeSpan DelayUntilNextRun(DateTimeOffset now, int hourLocal)
    {
        var delay = NextRun(now, hourLocal) - now;
        return delay < TimeSpan.Zero ? TimeSpan.Zero : delay;
    }

    public async Task SweepOnceAsync(CancellationToken cancellationToken)
    {
        // Resolved lazily so the repository (and its SQL connection) is only touched when a sweep runs.
        var repository = serviceProvider.GetRequiredService<IInventoryRepository>();

        logger.LogInformation("Inventory sweep starting for {ServerCount} servers", _options.Servers.Length);
        var succeeded = 0;
        var skipped = 0;
        var failed = 0;

        await FanOut.RunAsync<object?>(
            _options.Servers,
            remotingOptions.Value.MaxConcurrency,
            async server =>
            {
                // One server's failure (collection or SQL) must not abort the rest of the sweep.
                try
                {
                    var result = await collector.CollectAsync(server, InventoryCollector.AllSections, cancellationToken)
                        .ConfigureAwait(false);

                    // Never overwrite a good snapshot with an empty one from a dead server.
                    if (result.Outcomes.Any(o => o.Unreachable))
                    {
                        Interlocked.Increment(ref skipped);
                        logger.LogWarning("Sweep skipped unreachable server {Server}", server);
                        return null;
                    }

                    await repository.UpsertAsync(result.Snapshot, cancellationToken).ConfigureAwait(false);
                    Interlocked.Increment(ref succeeded);

                    // Semantic indexing is best-effort: an embedding/Qdrant outage must not
                    // count the server as failed — the SQL snapshot above is already saved.
                    try
                    {
                        await indexer.IndexAsync(result.Snapshot, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Semantic indexing failed for {Server}", server);
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref failed);
                    logger.LogError(ex, "Sweep failed for server {Server}", server);
                }

                return null;
            },
            cancellationToken).ConfigureAwait(false);

        logger.LogInformation(
            "Inventory sweep finished: {Succeeded} upserted, {Skipped} unreachable, {Failed} failed",
            succeeded, skipped, failed);
    }
}
