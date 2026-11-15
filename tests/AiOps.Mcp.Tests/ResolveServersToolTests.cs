using AiOps.Mcp.Directory;
using AiOps.Mcp.Tools.ResolveServers;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;
using NSubstitute;

namespace AiOps.Mcp.Tests;

public class ResolveServersToolTests
{
    private readonly IDirectoryGateway _directory = Substitute.For<IDirectoryGateway>();

    private Task<AiOps.Contracts.ResolveServersResult> Call(string filter)
        => ResolveServersTool.ResolveServers(_directory, NullLoggerFactory.Instance, filter);

    private void Respond(params AdComputer[] computers)
        => _directory.SearchComputersAsync(
                Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(computers);

    [Fact]
    public async Task Maps_and_sorts_results_preferring_fqdn()
    {
        Respond(
            new AdComputer("ZSRV", "zsrv.contoso.local", "Windows Server 2022", "CN=ZSRV,OU=Web,DC=contoso,DC=local"),
            new AdComputer("ASRV-NO-DNS", null, "Windows Server 2019", "CN=ASRV-NO-DNS,OU=Web,DC=contoso,DC=local"));

        var result = await Call("srv");

        Assert.Equal(["ASRV-NO-DNS", "zsrv.contoso.local"], result.Servers.Select(s => s.Name));
        Assert.Equal("Windows Server 2022", result.Servers[1].OperatingSystem);
        Assert.Contains("OU=Web", result.Servers[0].DistinguishedName);
        Assert.False(result.Truncated);
    }

    [Fact]
    public async Task Passes_the_ou_as_search_base_and_requests_one_extra_row()
    {
        Respond();

        await Call("OU=SQL,DC=contoso,DC=local");

        await _directory.Received(1).SearchComputersAsync(
            "OU=SQL,DC=contoso,DC=local",
            Arg.Is<string>(f => f.Contains("(objectCategory=computer)")),
            ResolveServersTool.MaxResults + 1,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Name_patterns_search_the_default_base()
    {
        Respond();

        await Call("SQL*");

        await _directory.Received(1).SearchComputersAsync(
            Arg.Is<string?>(b => b == null), Arg.Is<string>(f => f.Contains("(cn=SQL*)")),
            Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Results_beyond_the_cap_are_cut_and_flagged()
    {
        Respond(Enumerable.Range(0, ResolveServersTool.MaxResults + 1)
            .Select(i => new AdComputer($"SRV{i:D4}", $"srv{i:D4}.contoso.local", "Windows Server 2022", null))
            .ToArray());

        var result = await Call("srv");

        Assert.Equal(ResolveServersTool.MaxResults, result.Servers.Count);
        Assert.True(result.Truncated);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Rejects_blank_filter(string filter)
        => await Assert.ThrowsAsync<McpException>(() => Call(filter));

    [Fact]
    public async Task Ldap_failure_surfaces_as_a_clean_tool_error()
    {
        // A bad OU distinguished name throws a raw DirectoryOperationException; the tool must
        // present it as an McpException, not leak the internal exception.
        _directory.SearchComputersAsync(Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns<Task<IReadOnlyList<AdComputer>>>(_ =>
                throw new System.DirectoryServices.Protocols.DirectoryOperationException("The object does not exist."));

        var ex = await Assert.ThrowsAsync<McpException>(() => Call("OU=Nonexistent,DC=contoso,DC=local"));
        Assert.Contains("Directory search failed", ex.Message);
    }
}
