using System.Management.Automation;
using System.Xml;
using AiOps.Mcp.Remoting;
using AiOps.Mcp.Tools.GetEvents;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;
using NSubstitute;

namespace AiOps.Mcp.Tests;

public class GetEventsToolTests
{
    private static readonly DateTimeOffset Start = new(2026, 7, 1, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset End = new(2026, 7, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly IRemoteExecutor _executor = Substitute.For<IRemoteExecutor>();

    private sealed class FakeEvent
    {
        public DateTime TimeCreated { get; init; } = new(2026, 7, 1, 11, 0, 0, DateTimeKind.Utc);
        public int Id { get; init; } = 7001;
        public string LevelDisplayName { get; init; } = "Error";
        public string ProviderName { get; init; } = "Service Control Manager";
        public string? Message { get; init; } = "Service failed to start.";
    }

    private void SetupResponse(params ServerCallResult[] results)
        => _executor.InvokeManyAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<RemoteCommand>(), Arg.Any<CancellationToken>())
            .Returns(results);

    private Task<AiOps.Contracts.GetEventsResult> Call(
        string[]? servers = null, int? maxEvents = null, int[]? eventIds = null)
        => GetEventsTool.GetEvents(
            _executor, NullLoggerFactory.Instance,
            servers ?? ["srv1"], "System", Start, End,
            event_ids: eventIds, max_events: maxEvents ?? GetEventsTool.DefaultMaxEvents);

    [Fact]
    public async Task Maps_remote_events_to_dtos()
    {
        SetupResponse(new ServerCallResult
        {
            Server = "srv1",
            Status = ServerCallStatus.Ok,
            Output = [PSObject.AsPSObject(new FakeEvent())],
        });

        var result = await Call();

        var server = Assert.Single(result.Results);
        Assert.Equal("ok", server.Status);
        var evt = Assert.Single(server.Events);
        Assert.Equal(7001, evt.Id);
        Assert.Equal("Error", evt.Level);
        Assert.Equal("Service Control Manager", evt.Provider);
        Assert.Equal("Service failed to start.", evt.Message);
    }

    [Fact]
    public async Task Truncates_message_bodies_to_500_chars()
    {
        SetupResponse(new ServerCallResult
        {
            Server = "srv1",
            Status = ServerCallStatus.Ok,
            Output = [PSObject.AsPSObject(new FakeEvent { Message = new string('x', 2000) })],
        });

        var result = await Call();

        Assert.Equal(GetEventsTool.MaxMessageLength, result.Results[0].Events[0].Message!.Length);
    }

    [Fact]
    public async Task Client_side_cap_bounds_output_even_if_the_target_overshoots()
    {
        // Defense in depth: the target's -MaxEvents bounds it, but a misbehaving/overshooting
        // response must never map an unbounded stream into the LLM context.
        SetupResponse(new ServerCallResult
        {
            Server = "srv1",
            Status = ServerCallStatus.Ok,
            Output = Enumerable.Range(0, 50).Select(_ => PSObject.AsPSObject(new FakeEvent())).ToList(),
        });

        var result = await Call(maxEvents: 10);

        Assert.Equal(10, result.Results[0].Events.Count);
    }

    [Fact]
    public async Task Clamps_max_events_to_hard_ceiling()
    {
        RemoteCommand? sent = null;
        _executor.InvokeManyAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Do<RemoteCommand>(c => sent = c), Arg.Any<CancellationToken>())
            .Returns([new ServerCallResult { Server = "srv1", Status = ServerCallStatus.Ok }]);

        await Call(maxEvents: 999_999);

        Assert.NotNull(sent);
        Assert.Equal(GetEventsTool.MaxEventsCeiling, sent!.Parameters["MaxEvents"]);
    }

    [Fact]
    public async Task Sends_filter_xml_built_from_arguments()
    {
        RemoteCommand? sent = null;
        _executor.InvokeManyAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Do<RemoteCommand>(c => sent = c), Arg.Any<CancellationToken>())
            .Returns([new ServerCallResult { Server = "srv1", Status = ServerCallStatus.Ok }]);

        await Call(eventIds: [42]);

        Assert.Equal(FleetCommands.GetWinEvent, sent!.CommandName);
        // Must be a string: PSRP cannot round-trip an XmlDocument to the remote binder.
        var filterXml = Assert.IsType<string>(sent.Parameters["FilterXml"]);
        Assert.Contains("EventID=42", filterXml);
        Assert.Contains("Path=\"System\"", filterXml);
    }

    [Fact]
    public async Task Unreachable_server_is_reported_not_thrown()
    {
        SetupResponse(
            new ServerCallResult { Server = "srv1", Status = ServerCallStatus.Ok },
            new ServerCallResult { Server = "srv2", Status = ServerCallStatus.Unreachable, Error = "timeout" });

        var result = await Call(servers: ["srv1", "srv2"]);

        Assert.Equal("ok", result.Results[0].Status);
        Assert.Equal("unreachable", result.Results[1].Status);
        Assert.Equal("timeout", result.Results[1].Error);
    }

    [Fact]
    public async Task No_matching_events_error_becomes_empty_ok_result()
    {
        SetupResponse(new ServerCallResult
        {
            Server = "srv1",
            Status = ServerCallStatus.Error,
            Error = "No events were found that match the specified selection criteria.",
            ErrorId = "NoMatchingEventsFound,Microsoft.PowerShell.Commands.GetWinEventCommand",
        });

        var result = await Call();

        Assert.Equal("ok", result.Results[0].Status);
        Assert.Empty(result.Results[0].Events);
        Assert.Null(result.Results[0].Error);
    }

    [Fact]
    public async Task Truncation_never_splits_a_surrogate_pair()
    {
        // Char 500 is the middle of an emoji: a naive cut would leave a lone high surrogate.
        var message = new string('x', GetEventsTool.MaxMessageLength - 1) + "😀" + new string('y', 100);
        SetupResponse(new ServerCallResult
        {
            Server = "srv1",
            Status = ServerCallStatus.Ok,
            Output = [PSObject.AsPSObject(new FakeEvent { Message = message })],
        });

        var result = await Call();

        var truncated = result.Results[0].Events[0].Message!;
        Assert.Equal(GetEventsTool.MaxMessageLength - 1, truncated.Length);
        Assert.False(char.IsHighSurrogate(truncated[^1]));
    }

    [Fact]
    public async Task Tolerates_records_with_missing_properties()
    {
        SetupResponse(new ServerCallResult
        {
            Server = "srv1",
            Status = ServerCallStatus.Ok,
            Output = [PSObject.AsPSObject(new { Id = 5 })], // no TimeCreated/Level/Provider/Message
        });

        var result = await Call();

        var evt = Assert.Single(result.Results[0].Events);
        Assert.Equal(5, evt.Id);
        Assert.Null(evt.Message);
    }

    [Fact]
    public async Task Rejects_empty_server_list()
        => await Assert.ThrowsAsync<McpException>(() => Call(servers: []));

    [Fact]
    public async Task Rejects_inverted_time_window()
        => await Assert.ThrowsAsync<McpException>(() => GetEventsTool.GetEvents(
            _executor, NullLoggerFactory.Instance, ["srv1"], "System", End, Start));
}
