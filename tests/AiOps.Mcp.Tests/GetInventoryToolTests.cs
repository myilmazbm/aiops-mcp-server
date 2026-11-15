using System.Management.Automation;
using AiOps.Mcp.Inventory;
using AiOps.Mcp.Remoting;
using AiOps.Mcp.Tools.GetInventory;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;
using NSubstitute;

namespace AiOps.Mcp.Tests;

public class GetInventoryToolTests
{
    private readonly IRemoteExecutor _executor = Substitute.For<IRemoteExecutor>();

    private Task<AiOps.Contracts.GetInventoryResult> Call(string server = "srv1", params string[] sections)
        => GetInventoryTool.GetInventory(
            new InventoryCollector(_executor), NullLoggerFactory.Instance,
            server, sections.Length > 0 ? sections : ["services"]);

    private void Respond(Func<RemoteCommand, ServerCallResult> responder)
        => _executor.InvokeAsync(Arg.Any<string>(), Arg.Any<RemoteCommand>(), Arg.Any<CancellationToken>())
            .Returns(call => responder(call.Arg<RemoteCommand>()));

    [Fact]
    public async Task All_sections_ok_yields_ok_status()
    {
        Respond(_ => new ServerCallResult
        {
            Server = "srv1",
            Status = ServerCallStatus.Ok,
            Output = [PSObject.AsPSObject(new { Name = "WinRM", Status = "Running" })],
        });

        var result = await Call(sections: ["services", "tasks"]);

        Assert.Equal("ok", result.Status);
        Assert.Null(result.SectionErrors);
        Assert.NotNull(result.Snapshot.Services);
    }

    [Fact]
    public async Task Mixed_failures_yield_partial_with_section_errors()
    {
        Respond(c => c.CommandName == FleetCommands.GetService
            ? new ServerCallResult { Server = "srv1", Status = ServerCallStatus.Ok, Output = [PSObject.AsPSObject(new { Name = "W32Time" })] }
            : new ServerCallResult { Server = "srv1", Status = ServerCallStatus.Error, Error = "access denied" });

        var result = await Call(sections: ["services", "hotfix"]);

        Assert.Equal("partial", result.Status);
        Assert.Equal("access denied", result.SectionErrors!["hotfix"]);
    }

    [Fact]
    public async Task All_sections_failing_yields_error_status()
    {
        Respond(_ => new ServerCallResult { Server = "srv1", Status = ServerCallStatus.Error, Error = "boom" });

        var result = await Call(sections: ["services", "hotfix"]);

        Assert.Equal("error", result.Status);
        Assert.Equal(2, result.SectionErrors!.Count);
    }

    [Fact]
    public async Task Unreachable_server_yields_unreachable_status()
    {
        Respond(_ => new ServerCallResult { Server = "srv1", Status = ServerCallStatus.Unreachable, Error = "timeout" });

        var result = await Call();

        Assert.Equal("unreachable", result.Status);
    }

    [Fact]
    public async Task Truncated_sections_are_reported()
    {
        Respond(_ => new ServerCallResult
        {
            Server = "srv1",
            Status = ServerCallStatus.Ok,
            Output = Enumerable.Range(0, InventoryCollector.MaxRowsPerSection + 1)
                .Select(i => PSObject.AsPSObject(new { Name = $"svc{i}" })).ToList(),
        });

        var result = await Call();

        Assert.Equal(["services"], result.TruncatedSections);
    }

    [Fact]
    public async Task Duplicate_and_differently_cased_sections_collapse()
    {
        var calls = 0;
        _executor.InvokeAsync(Arg.Any<string>(), Arg.Any<RemoteCommand>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                Interlocked.Increment(ref calls);
                return new ServerCallResult { Server = "srv1", Status = ServerCallStatus.Ok };
            });

        await Call(sections: ["services", "SERVICES", " services "]);

        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Rejects_blank_server(string server)
        => await Assert.ThrowsAsync<McpException>(() => Call(server: server));

    [Fact]
    public async Task Rejects_unknown_sections_with_guidance()
    {
        var ex = await Assert.ThrowsAsync<McpException>(() => Call(sections: ["services", "netstat"]));

        Assert.Contains("netstat", ex.Message);
        Assert.Contains("services|software|ports", ex.Message);
    }

    [Fact]
    public async Task Rejects_empty_section_list()
        => await Assert.ThrowsAsync<McpException>(() => GetInventoryTool.GetInventory(
            new InventoryCollector(_executor), NullLoggerFactory.Instance, "srv1", []));
}
