using System.ComponentModel;
using System.Diagnostics;
using AiOps.Contracts;
using AiOps.Mcp.Inventory;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace AiOps.Mcp.Tools.GetInventory;

[McpServerToolType]
public static class GetInventoryTool
{
    [McpServerTool(Name = "get_inventory", ReadOnly = true)]
    [Description("Reads live inventory sections from one server: services, software (registry Uninstall keys), listening ports, applied GPOs, hotfixes, machine certificates, scheduled tasks. Each section is capped at 500 rows.")]
    public static async Task<GetInventoryResult> GetInventory(
        InventoryCollector collector,
        ILoggerFactory loggerFactory,
        [Description("Target server name (FQDN)")] string server,
        [Description("Sections to collect: services|software|ports|gpo|hotfix|certs|tasks")] string[] sections,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(server))
            throw new McpException("server is required.");
        if (sections is not { Length: > 0 })
            throw new McpException("sections must contain at least one section name.");

        var normalized = sections
            .Select(s => s.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var unknown = normalized
            .Where(s => !InventoryCollector.AllSections.Contains(s, StringComparer.OrdinalIgnoreCase))
            .ToList();
        if (unknown.Count > 0)
            throw new McpException(
                $"Unknown section(s): {string.Join(", ", unknown)}. Valid: {string.Join("|", InventoryCollector.AllSections)}");

        var stopwatch = Stopwatch.StartNew();
        var result = await collector.CollectAsync(server, normalized, cancellationToken).ConfigureAwait(false);

        var failed = result.Outcomes.Where(o => o.Error is not null).ToList();
        var status =
            result.Outcomes.Any(o => o.Unreachable) ? "unreachable" :
            failed.Count == 0 ? "ok" :
            failed.Count == result.Outcomes.Count ? "error" : "partial";

        var truncated = result.Outcomes.Where(o => o.Truncated).Select(o => o.Section).ToList();

        loggerFactory.CreateLogger("AiOps.Mcp.Tools.get_inventory").LogInformation(
            "get_inventory server={Server} sections={Sections} status={Status} took {ElapsedMs} ms",
            server, string.Join(",", normalized), status, stopwatch.ElapsedMilliseconds);

        return new GetInventoryResult
        {
            Server = server,
            Status = status,
            SectionErrors = failed.Count > 0 ? failed.ToDictionary(o => o.Section, o => o.Error!) : null,
            TruncatedSections = truncated.Count > 0 ? truncated : null,
            Snapshot = result.Snapshot,
        };
    }
}
