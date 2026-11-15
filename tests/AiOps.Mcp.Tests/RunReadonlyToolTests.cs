using System.Management.Automation;
using System.Text.Json;
using AiOps.Mcp.Remoting;
using AiOps.Mcp.Tools.RunReadonly;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;
using NSubstitute;

namespace AiOps.Mcp.Tests;

public class RunReadonlyToolTests
{
    private readonly IRemoteExecutor _executor = Substitute.For<IRemoteExecutor>();
    private RemoteCommand? _sent;

    private void Respond(ServerCallResult result)
        => _executor.InvokeAsync(Arg.Any<string>(), Arg.Do<RemoteCommand>(c => _sent = c), Arg.Any<CancellationToken>())
            .Returns(result);

    private static ServerCallResult Ok(params object[] items) => new()
    {
        Server = "srv1",
        Status = ServerCallStatus.Ok,
        Output = items.Select(PSObject.AsPSObject).ToList(),
    };

    private static Dictionary<string, JsonElement> Params(string json)
        => JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;

    private Task<AiOps.Contracts.RunReadonlyResult> Call(
        string command = "Get-Service", Dictionary<string, JsonElement>? parameters = null, string server = "srv1")
        => RunReadonlyTool.RunReadonly(_executor, NullLoggerFactory.Instance, server, command, parameters);

    // Security invariant #1: the MCP layer must not judge commands. Any command — including
    // write cmdlets and injection-shaped strings — is forwarded verbatim (name + params) for
    // the JEA endpoint to accept or reject. A partial client-side blocklist regression must
    // fail this test.
    [Theory]
    [InlineData("Set-Service")]
    [InlineData("Invoke-Expression")]
    [InlineData("Start-Process")]
    [InlineData("Remove-Item")]
    [InlineData("Get-Service; Remove-Item C:\\")]
    [InlineData("Get-Process | Stop-Process")]
    public async Task Command_and_parameters_pass_through_verbatim(string command)
    {
        Respond(Ok());

        await Call(command: command, parameters: Params("""{"Name":"WinRM","Force":true}"""));

        Assert.Equal(command.Trim(), _sent!.CommandName);
        Assert.Equal("WinRM", _sent.Parameters["Name"]);
        Assert.Equal(true, _sent.Parameters["Force"]);
    }

    [Fact]
    public async Task A_bounded_output_backstop_is_appended_on_the_target()
    {
        Respond(Ok());

        await Call(command: "Get-Service");

        // Structural, not woven into the command text: bounds output ON THE TARGET so a
        // caller cannot make the executor buffer an unbounded stream into MCP memory.
        Assert.Equal(@"Microsoft.PowerShell.Utility\Select-Object", _sent!.Pipe!.CommandName);
        Assert.Equal(RunReadonlyTool.MaxRows + 1, _sent.Pipe.Parameters["First"]);
    }

    [Fact]
    public async Task Json_parameters_convert_to_clr_types()
    {
        Respond(Ok());

        await Call(parameters: Params("""
            {"Name":"WinRM","Count":5,"Ratio":2.5,"Force":true,"Ids":[1,2,3]}
            """));

        Assert.Equal("WinRM", _sent!.Parameters["Name"]);
        Assert.Equal(5L, _sent.Parameters["Count"]);
        Assert.Equal(2.5, _sent.Parameters["Ratio"]);
        Assert.Equal(true, _sent.Parameters["Force"]);
        Assert.Equal(new object[] { 1L, 2L, 3L }, _sent.Parameters["Ids"]);
    }

    [Fact]
    public async Task Nested_object_parameters_are_rejected()
        => await Assert.ThrowsAsync<McpException>(
            () => Call(parameters: Params("""{"Filter":{"Nested":1}}""")));

    [Fact]
    public async Task Nested_arrays_are_rejected()
        => await Assert.ThrowsAsync<McpException>(
            () => Call(parameters: Params("""{"Matrix":[[1,2]]}""")));

    [Fact]
    public async Task Null_parameter_values_are_rejected_not_silently_dropped()
        // A JSON null would bind nothing (a different query than asked) while the audit log
        // still lists the key; reject it like a nested object.
        => await Assert.ThrowsAsync<McpException>(
            () => Call(parameters: Params("""{"Name":null}""")));

    [Fact]
    public async Task Output_is_capped_at_the_hard_row_limit()
    {
        Respond(Ok(Enumerable.Range(0, RunReadonlyTool.MaxRows + 10)
            .Select(object (i) => new { Name = $"row{i}" }).ToArray()));

        var result = await Call();

        Assert.Equal(RunReadonlyTool.MaxRows, result.Rows.Count);
        Assert.True(result.Truncated);
    }

    [Fact]
    public async Task Values_are_truncated_to_the_cap()
    {
        Respond(Ok(new { Message = new string('x', 5000) }));

        var result = await Call();

        Assert.Equal(RunReadonlyTool.MaxValueLength, result.Rows[0]["Message"]!.Length);
    }

    [Fact]
    public async Task Primitive_output_becomes_a_value_row()
    {
        Respond(Ok("plain text", 42));

        var result = await Call();

        Assert.Equal("plain text", result.Rows[0]["value"]);
        Assert.Equal("42", result.Rows[1]["value"]);
    }

    [Fact]
    public async Task Dates_are_formatted_invariantly()
    {
        Respond(Ok(new { When = new DateTime(2026, 7, 6, 10, 30, 0, DateTimeKind.Utc) }));

        var result = await Call();

        Assert.Equal("2026-07-06T10:30:00.0000000Z", result.Rows[0]["When"]);
    }

    [Fact]
    public async Task Collection_values_render_as_joined_preview_not_type_name()
    {
        Respond(Ok(new { DnsNames = new[] { "a.contoso.local", "b.contoso.local" } }));

        var result = await Call();

        Assert.Equal("a.contoso.local, b.contoso.local", result.Rows[0]["DnsNames"]);
    }

    private sealed class Booby
    {
        public string Fine => "ok";
        public string Trap => throw new InvalidOperationException("adapter blew up");
    }

    [Fact]
    public async Task Throwing_property_getters_are_skipped_not_fatal()
    {
        Respond(Ok(new Booby()));

        var result = await Call();

        Assert.Equal("ok", result.Rows[0]["Fine"]);
        Assert.False(result.Rows[0].ContainsKey("Trap"));
    }

    [Fact]
    public async Task Property_count_per_row_is_bounded()
    {
        var wide = new Dictionary<string, object?>();
        var psObj = new PSObject();
        for (var i = 0; i < 100; i++)
            psObj.Properties.Add(new PSNoteProperty($"p{i:D3}", i));
        _executor.InvokeAsync(Arg.Any<string>(), Arg.Any<RemoteCommand>(), Arg.Any<CancellationToken>())
            .Returns(new ServerCallResult { Server = "srv1", Status = ServerCallStatus.Ok, Output = [psObj] });
        _ = wide;

        var result = await Call();

        Assert.Equal(RunReadonlyTool.MaxPropertiesPerRow, result.Rows[0].Count);
    }

    [Fact]
    public async Task Unreachable_maps_to_unreachable_status()
    {
        Respond(new ServerCallResult { Server = "srv1", Status = ServerCallStatus.Unreachable, Error = "timeout" });

        var result = await Call();

        Assert.Equal("unreachable", result.Status);
        Assert.Empty(result.Rows);
    }

    [Theory]
    [InlineData("", "Get-Service")]
    [InlineData("srv1", "")]
    [InlineData("srv1", "   ")]
    public async Task Rejects_blank_server_or_command(string server, string command)
        => await Assert.ThrowsAsync<McpException>(() => Call(command: command, server: server));
}
