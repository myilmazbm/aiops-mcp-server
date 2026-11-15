using System.Net.Http.Json;
using System.Text.Json;
using AiOps.Contracts;
using Microsoft.Extensions.Options;

namespace AiOps.Mcp.Alerts;

/// <summary>
/// Drains the alert queue and POSTs each alert to the separate orchestrator service.
/// Transient failures retry a few times; a permanently failing alert is logged and
/// dropped so the queue keeps moving.
/// </summary>
public sealed class AlertForwarderService(
    AlertQueue queue,
    IHttpClientFactory httpClientFactory,
    IOptions<AlertsOptions> options,
    ILogger<AlertForwarderService> logger) : BackgroundService
{
    public const string HttpClientName = "scom-alert-forwarder";

    private readonly AlertsOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (string.IsNullOrEmpty(_options.ForwardUrl))
        {
            logger.LogWarning("Alerts:ForwardUrl is not configured; incoming alerts are accepted and logged only");
            // Still drain so the queue never fills with unforwardable alerts.
            await foreach (var alert in queue.ReadAllAsync(stoppingToken).ConfigureAwait(false))
                logger.LogInformation("Alert {AlertId} ({Name}) accepted; no forwarder configured", alert.AlertId, alert.Name);
            return;
        }

        await foreach (var alert in queue.ReadAllAsync(stoppingToken).ConfigureAwait(false))
        {
            var forwarded = await TryForwardAsync(alert, stoppingToken).ConfigureAwait(false);
            if (!forwarded)
                logger.LogError("Alert {AlertId} ({Name}) dropped after {Attempts} forward attempts",
                    alert.AlertId, alert.Name, _options.ForwardAttempts);
        }
    }

    internal async Task<bool> TryForwardAsync(ScomAlert alert, CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= Math.Max(1, _options.ForwardAttempts); attempt++)
        {
            try
            {
                using var client = httpClientFactory.CreateClient(HttpClientName);
                using var response = await client.PostAsJsonAsync(_options.ForwardUrl, alert, JsonSerializerOptions.Web, cancellationToken)
                    .ConfigureAwait(false);

                if (response.IsSuccessStatusCode)
                    return true;

                logger.LogWarning("Forwarding alert {AlertId} attempt {Attempt} got HTTP {Status}",
                    alert.AlertId, attempt, (int)response.StatusCode);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Forwarding alert {AlertId} attempt {Attempt} failed", alert.AlertId, attempt);
            }

            if (attempt < _options.ForwardAttempts)
            {
                // Clamp: a negative/overflowing configured delay would make Task.Delay throw
                // ArgumentOutOfRangeException outside the try above and fault the host.
                var delayMs = Math.Clamp((long)_options.ForwardRetryDelayMs * attempt, 0, 300_000);
                await Task.Delay(TimeSpan.FromMilliseconds(delayMs), cancellationToken).ConfigureAwait(false);
            }
        }

        return false;
    }
}
