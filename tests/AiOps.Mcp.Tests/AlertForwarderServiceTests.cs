using System.Net;
using System.Text.Json;
using AiOps.Contracts;
using AiOps.Mcp.Alerts;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace AiOps.Mcp.Tests;

public class AlertForwarderServiceTests
{
    private sealed class SequenceHandler(params HttpStatusCode[] statuses) : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];
        private int _call;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            var status = statuses[Math.Min(_call, statuses.Length - 1)];
            _call++;
            return new HttpResponseMessage(status);
        }
    }

    private static ScomAlert Alert(string id = "a1") => new()
    {
        AlertId = id,
        Name = "Disk full",
        Severity = "Critical",
        ReceivedAt = DateTimeOffset.UtcNow,
    };

    private (AlertForwarderService Service, AlertQueue Queue) CreateService(HttpMessageHandler handler, int retryDelayMs = 0)
    {
        var options = Options.Create(new AlertsOptions
        {
            ForwardUrl = "http://orchestrator:5000/alerts",
            ForwardAttempts = 3,
            ForwardRetryDelayMs = retryDelayMs,
        });
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(handler, disposeHandler: false));

        var queue = new AlertQueue(options, NullLogger<AlertQueue>.Instance);
        var service = new AlertForwarderService(queue, factory, options, NullLogger<AlertForwarderService>.Instance);
        return (service, queue);
    }

    [Fact]
    public async Task Negative_retry_delay_is_clamped_not_thrown()
    {
        // A misconfigured negative delay would make Task.Delay throw outside the try and
        // fault the host; the clamp keeps retries running.
        var handler = new SequenceHandler(HttpStatusCode.InternalServerError, HttpStatusCode.OK);
        var (service, _) = CreateService(handler, retryDelayMs: -5000);

        var forwarded = await service.TryForwardAsync(Alert(), CancellationToken.None);

        Assert.True(forwarded);
        Assert.Equal(2, handler.Bodies.Count);
    }

    [Fact]
    public async Task Forwards_the_alert_as_json_on_first_success()
    {
        var handler = new SequenceHandler(HttpStatusCode.OK);
        var (service, _) = CreateService(handler);

        var forwarded = await service.TryForwardAsync(Alert(), CancellationToken.None);

        Assert.True(forwarded);
        var body = Assert.Single(handler.Bodies);
        using var json = JsonDocument.Parse(body);
        Assert.Equal("a1", json.RootElement.GetProperty("alertId").GetString());
        Assert.Equal("Critical", json.RootElement.GetProperty("severity").GetString());
    }

    [Fact]
    public async Task Retries_transient_failures_then_succeeds()
    {
        var handler = new SequenceHandler(HttpStatusCode.InternalServerError, HttpStatusCode.OK);
        var (service, _) = CreateService(handler);

        var forwarded = await service.TryForwardAsync(Alert(), CancellationToken.None);

        Assert.True(forwarded);
        Assert.Equal(2, handler.Bodies.Count);
    }

    [Fact]
    public async Task Gives_up_after_the_configured_attempts()
    {
        var handler = new SequenceHandler(HttpStatusCode.InternalServerError);
        var (service, _) = CreateService(handler);

        var forwarded = await service.TryForwardAsync(Alert(), CancellationToken.None);

        Assert.False(forwarded);
        Assert.Equal(3, handler.Bodies.Count);
    }

    [Fact]
    public async Task A_poisonous_alert_does_not_stall_the_queue()
    {
        // First alert fails all attempts; the second must still be delivered.
        var handler = new SequenceHandler(
            HttpStatusCode.InternalServerError, HttpStatusCode.InternalServerError, HttpStatusCode.InternalServerError,
            HttpStatusCode.OK);
        var (service, queue) = CreateService(handler);

        queue.Enqueue(Alert("poison"));
        queue.Enqueue(Alert("healthy"));

        await service.StartAsync(CancellationToken.None);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (handler.Bodies.Count < 4 && DateTime.UtcNow < deadline)
            await Task.Delay(20);
        await service.StopAsync(CancellationToken.None);

        Assert.Equal(4, handler.Bodies.Count);
        Assert.Contains("healthy", handler.Bodies[^1]);
    }

    [Fact]
    public async Task Queue_overflow_drops_the_oldest_alert()
    {
        var options = Options.Create(new AlertsOptions { QueueCapacity = 2 });
        var queue = new AlertQueue(options, NullLogger<AlertQueue>.Instance);

        queue.Enqueue(Alert("first"));
        queue.Enqueue(Alert("second"));
        queue.Enqueue(Alert("third")); // capacity 2: "first" is dropped

        Assert.Equal(1, queue.DroppedCount);
        var remaining = new List<string>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        await foreach (var alert in queue.ReadAllAsync(cts.Token))
        {
            remaining.Add(alert.AlertId);
            if (remaining.Count == 2)
                break;
        }

        Assert.Equal(["second", "third"], remaining);
    }
}
