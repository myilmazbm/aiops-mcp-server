using System.Text.Json;
using Microsoft.Extensions.Options;

namespace AiOps.Mcp.Alerts;

/// <summary>
/// POST /webhooks/scom — the only HTTP surface of the SCOM integration in this repo;
/// the orchestration itself lives in a separate service (fed via the forwarder).
/// </summary>
public static class ScomWebhookHandler
{
    public static async Task<IResult> HandleAsync(
        HttpContext context,
        AlertQueue queue,
        IOptions<AlertsOptions> optionsAccessor,
        ILoggerFactory loggerFactory)
    {
        var options = optionsAccessor.Value;
        var logger = loggerFactory.CreateLogger("AiOps.Mcp.Alerts.Webhook");

        var remoteIp = AiOps.Mcp.Http.ClientAddress.Of(context.Connection.RemoteIpAddress);
        if (!AiOps.Mcp.Http.ClientAddress.IsAllowed(remoteIp, options.AllowedSourceIps))
        {
            logger.LogWarning("Rejected SCOM webhook from disallowed source {RemoteIp}", remoteIp ?? "(unknown)");
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }

        if (context.Request.ContentLength > options.MaxBodyBytes)
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);

        var body = await ReadCappedAsync(context.Request.Body, options.MaxBodyBytes, context.RequestAborted)
            .ConfigureAwait(false);
        if (body is null)
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            return Results.BadRequest(new { error = "Body is not valid JSON." });
        }

        using (document)
        {
            var (alert, error) = ScomAlertNormalizer.Normalize(document.RootElement, DateTimeOffset.UtcNow);
            if (alert is null)
                return Results.BadRequest(new { error });

            queue.Enqueue(alert);
            logger.LogInformation(
                "Accepted SCOM alert {AlertId} ({Name}) severity={Severity} server={Server} from {RemoteIp}",
                alert.AlertId, alert.Name, alert.Severity, alert.Server, remoteIp ?? "(unknown)");

            return Results.Accepted(value: new { alertId = alert.AlertId });
        }
    }

    /// <summary>Reads at most maxBytes; returns null when the stream has more (Content-Length can lie).</summary>
    private static async Task<byte[]?> ReadCappedAsync(Stream body, int maxBytes, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        while (true)
        {
            var read = await body.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (read == 0)
                return buffer.ToArray();

            if (buffer.Length + read > maxBytes)
                return null;

            buffer.Write(chunk, 0, read);
        }
    }
}
