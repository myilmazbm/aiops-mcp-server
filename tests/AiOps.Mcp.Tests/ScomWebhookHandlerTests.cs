using System.Net;
using System.Text;
using AiOps.Contracts;
using AiOps.Mcp.Alerts;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AiOps.Mcp.Tests;

public class ScomWebhookHandlerTests
{
    private readonly AlertsOptions _options = new();
    private AlertQueue _queue = null!;

    private async Task<(int StatusCode, ScomAlert? Queued)> Post(
        string body, string? remoteIp = "10.0.0.5", long? contentLength = null)
    {
        _queue = new AlertQueue(Options.Create(_options), NullLogger<AlertQueue>.Instance);

        var context = new DefaultHttpContext();
        var bytes = Encoding.UTF8.GetBytes(body);
        context.Request.Body = new MemoryStream(bytes);
        context.Request.ContentLength = contentLength ?? bytes.Length;
        context.Connection.RemoteIpAddress = remoteIp is null ? null : IPAddress.Parse(remoteIp);

        var result = await ScomWebhookHandler.HandleAsync(
            context, _queue, Options.Create(_options), NullLoggerFactory.Instance);

        var statusCode = ((Microsoft.AspNetCore.Http.IStatusCodeHttpResult)result).StatusCode!.Value;

        ScomAlert? queued = null;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        if (statusCode == StatusCodes.Status202Accepted)
        {
            await foreach (var alert in _queue.ReadAllAsync(cts.Token))
            {
                queued = alert;
                break;
            }
        }

        return (statusCode, queued);
    }

    [Fact]
    public async Task Valid_alert_is_accepted_and_queued()
    {
        var (status, queued) = await Post("""{"alertName":"Disk full","severity":2,"computer":"SRV1"}""");

        Assert.Equal(202, status);
        Assert.Equal("Disk full", queued!.Name);
        Assert.Equal("Error", queued.Severity);
    }

    [Fact]
    public async Task Invalid_json_is_a_bad_request()
    {
        var (status, _) = await Post("this is not json {");

        Assert.Equal(400, status);
    }

    [Fact]
    public async Task Nameless_payload_is_a_bad_request()
    {
        var (status, _) = await Post("""{"severity":2}""");

        Assert.Equal(400, status);
    }

    [Fact]
    public async Task Declared_oversize_body_is_rejected_up_front()
    {
        _options.MaxBodyBytes = 100;

        var (status, _) = await Post("""{"alertName":"x"}""", contentLength: 5000);

        Assert.Equal(413, status);
    }

    [Fact]
    public async Task Lying_content_length_is_caught_while_reading()
    {
        _options.MaxBodyBytes = 50;
        var big = "{\"alertName\":\"" + new string('x', 500) + "\"}";

        // Declared length under the cap, actual body far over it.
        var (status, _) = await Post(big, contentLength: 10);

        Assert.Equal(413, status);
    }

    [Fact]
    public async Task Disallowed_source_ip_is_forbidden()
    {
        _options.AllowedSourceIps = ["10.0.0.99"];

        var (status, _) = await Post("""{"alertName":"x"}""", remoteIp: "10.0.0.5");

        Assert.Equal(403, status);
    }

    [Fact]
    public async Task Ipv4_mapped_ipv6_source_matches_a_plain_ipv4_allowlist()
    {
        _options.AllowedSourceIps = ["10.0.0.5"];

        // Kestrel reports IPv4 clients this way on dual-stack listeners.
        var (status, queued) = await Post("""{"alertName":"x"}""", remoteIp: "::ffff:10.0.0.5");

        Assert.Equal(202, status);
        Assert.NotNull(queued);
    }

    [Fact]
    public async Task Unknown_remote_with_allowlist_is_forbidden()
    {
        _options.AllowedSourceIps = ["10.0.0.5"];

        var (status, _) = await Post("""{"alertName":"x"}""", remoteIp: null);

        Assert.Equal(403, status);
    }

    [Fact]
    public async Task Empty_allowlist_accepts_any_source()
    {
        var (status, _) = await Post("""{"alertName":"x"}""", remoteIp: "192.168.1.1");

        Assert.Equal(202, status);
    }
}
