using System.ComponentModel;
using System.Diagnostics;
using System.Management.Automation;
using AiOps.Contracts;
using AiOps.Mcp.Remoting;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace AiOps.Mcp.Tools.GetEvents;

[McpServerToolType]
public static class GetEventsTool
{
    public const int DefaultMaxEvents = 200;
    /// <summary>Hard ceiling; requests above it are clamped, never honored.</summary>
    public const int MaxEventsCeiling = 1000;
    /// <summary>Event message bodies are truncated to this many characters.</summary>
    public const int MaxMessageLength = 500;

    [McpServerTool(Name = "get_events", ReadOnly = true)]
    [Description("Queries Windows event logs on one or more servers. Filtering runs on the target server via Get-WinEvent -FilterXml. Message bodies are truncated to 500 characters.")]
    public static async Task<GetEventsResult> GetEvents(
        IRemoteExecutor executor,
        ILoggerFactory loggerFactory,
        [Description("Target server names (FQDN)")] string[] servers,
        [Description("Event log channel, e.g. 'System' or 'Microsoft-Windows-GroupPolicy/Operational'")] string channel,
        [Description("Start of the time window (ISO-8601)")] DateTimeOffset start,
        [Description("End of the time window (ISO-8601)")] DateTimeOffset end,
        [Description("Optional event IDs to match")] int[]? event_ids = null,
        [Description("Optional level: 1=Critical, 2=Error, 3=Warning, 4=Information")] int? level = null,
        [Description("Optional raw XPath; overrides event_ids/level/time filters")] string? xpath = null,
        [Description("Maximum events per server (default 200, hard cap 1000)")] int max_events = DefaultMaxEvents,
        CancellationToken cancellationToken = default)
    {
        if (servers is not { Length: > 0 })
            throw new McpException("servers must contain at least one server name.");
        if (string.IsNullOrWhiteSpace(channel))
            throw new McpException("channel is required.");
        if (end <= start)
            throw new McpException("end must be after start.");

        var maxEvents = Math.Clamp(max_events, 1, MaxEventsCeiling);

        // Sent as a string: PSRP would flatten an XmlDocument into a property bag the remote
        // binder cannot use, while a string is converted to XmlDocument on the target.
        var filterXml = EventFilterXmlBuilder.Build(channel, event_ids, level, start, end, xpath);

        var command = RemoteCommand.Create(
            FleetCommands.GetWinEvent,
            ("FilterXml", filterXml),
            ("MaxEvents", maxEvents),
            ("ErrorAction", "Stop"));

        var stopwatch = Stopwatch.StartNew();
        var callResults = await executor.InvokeManyAsync(servers, command, cancellationToken).ConfigureAwait(false);
        var results = callResults.Select(call => MapServerResult(call, maxEvents)).ToList();

        loggerFactory.CreateLogger("AiOps.Mcp.Tools.get_events").LogInformation(
            "get_events channel={Channel} servers={ServerCount} maxEvents={MaxEvents} took {ElapsedMs} ms, {EventCount} events",
            channel, servers.Length, maxEvents, stopwatch.ElapsedMilliseconds, results.Sum(r => r.Events.Count));

        return new GetEventsResult { Results = results };
    }

    private static ServerEventsResult MapServerResult(ServerCallResult call, int maxEvents)
    {
        // Get-WinEvent reports "no events matched" as an error; that is an empty result, not a failure.
        if (call.Status == ServerCallStatus.Error && call.ErrorId?.StartsWith("NoMatchingEventsFound", StringComparison.Ordinal) == true)
            return new ServerEventsResult { Server = call.Server, Status = "ok" };

        if (call.Status != ServerCallStatus.Ok)
        {
            return new ServerEventsResult
            {
                Server = call.Server,
                Status = call.Status.ToWire(),
                Error = call.Error,
            };
        }

        return new ServerEventsResult
        {
            Server = call.Server,
            Status = "ok",
            // Client-side backstop for the mandatory cap: the target's -MaxEvents bounds it,
            // but the MCP side must never map an unbounded stream into the LLM context.
            Events = call.Output.Take(maxEvents).Select(MapEvent).ToList(),
        };
    }

    private static EventRecordDto MapEvent(PSObject record)
    {
        var message = PsValue.Get<string>(record, "Message");
        if (message is not null)
            message = TextUtil.Truncate(message, MaxMessageLength);

        var timeCreated = PsValue.Get<DateTime>(record, "TimeCreated");
        return new EventRecordDto
        {
            // default(DateTime) + a positive local offset would underflow DateTimeOffset
            TimeCreated = timeCreated == default ? DateTimeOffset.MinValue : timeCreated,
            Id = PsValue.Get<int>(record, "Id"),
            Level = PsValue.Get<string>(record, "LevelDisplayName"),
            Provider = PsValue.Get<string>(record, "ProviderName"),
            Message = message,
        };
    }
}
