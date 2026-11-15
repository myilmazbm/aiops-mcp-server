using System.ComponentModel;
using System.Diagnostics;
using AiOps.Contracts;
using AiOps.Mcp.Directory;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace AiOps.Mcp.Tools.ResolveServers;

[McpServerToolType]
public static class ResolveServersTool
{
    /// <summary>Hard result cap; mandatory and not exposed as a parameter.</summary>
    public const int MaxResults = 500;

    [McpServerTool(Name = "resolve_servers", ReadOnly = true)]
    [Description("Resolves fleet servers from Active Directory. The filter is either a name pattern ('SQL*', 'web' — no wildcard means contains-match) or an OU distinguished name ('OU=Databases,DC=contoso,DC=local'). Returns server-OS computer accounts only, capped at 500.")]
    public static async Task<ResolveServersResult> ResolveServers(
        IDirectoryGateway directory,
        ILoggerFactory loggerFactory,
        [Description("Name pattern or OU distinguished name")] string filter,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(filter))
            throw new McpException("filter is required.");

        var (searchBase, ldapFilter) = LdapQueryBuilder.Build(filter);

        var stopwatch = Stopwatch.StartNew();
        // Ask for one extra row to detect truncation at the cap.
        IReadOnlyList<AiOps.Mcp.Directory.AdComputer> computers;
        try
        {
            computers = await directory.SearchComputersAsync(searchBase, ldapFilter, MaxResults + 1, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (System.DirectoryServices.Protocols.DirectoryException ex)
        {
            // A bad OU distinguished name (e.g. "OU=" or a non-existent base) surfaces as a
            // raw LDAP exception; turn it into a clean tool error the LLM can act on.
            throw new McpException($"Directory search failed for filter '{filter}': {ex.Message}");
        }

        var truncated = computers.Count > MaxResults;
        var servers = computers
            .Take(MaxResults)
            .Select(c => new ResolvedServer
            {
                Name = c.DnsHostName ?? c.Name,
                OperatingSystem = c.OperatingSystem,
                DistinguishedName = c.DistinguishedName,
            })
            .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        loggerFactory.CreateLogger("AiOps.Mcp.Tools.resolve_servers").LogInformation(
            "resolve_servers filter={Filter} base={SearchBase} results={Count} took {ElapsedMs} ms",
            filter, searchBase ?? "(default)", servers.Count, stopwatch.ElapsedMilliseconds);

        return new ResolveServersResult { Servers = servers, Truncated = truncated };
    }
}
