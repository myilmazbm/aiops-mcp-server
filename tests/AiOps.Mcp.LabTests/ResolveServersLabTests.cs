using AiOps.Mcp.Directory;
using AiOps.Mcp.Tools.ResolveServers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AiOps.Mcp.LabTests;

/// <summary>
/// Integration tests against the lab domain's Active Directory. Requires a
/// domain-joined machine; set AIOPS_AD_FILTER (e.g. a known name pattern) to enable.
/// Not run in CI.
/// </summary>
public class ResolveServersLabTests
{
    private static string? Filter => Environment.GetEnvironmentVariable("AIOPS_AD_FILTER");

    [SkippableFact]
    public async Task Resolves_servers_from_the_lab_domain()
    {
        Skip.If(string.IsNullOrEmpty(Filter), "AIOPS_AD_FILTER not set");

        var gateway = new LdapDirectoryGateway(Options.Create(new DirectoryOptions()));

        var result = await ResolveServersTool.ResolveServers(
            gateway, NullLoggerFactory.Instance, Filter!);

        Assert.NotEmpty(result.Servers);
        Assert.All(result.Servers, s => Assert.False(string.IsNullOrEmpty(s.Name)));
        Assert.All(result.Servers, s => Assert.Contains("Server", s.OperatingSystem ?? "Server"));
    }
}
