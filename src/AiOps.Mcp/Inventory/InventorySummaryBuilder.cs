using System.Text;
using AiOps.Contracts;

namespace AiOps.Mcp.Inventory;

/// <summary>
/// Renders a snapshot as compact, deterministic English text for embedding (bge-m3).
/// Deterministic output matters: an unchanged server must produce an identical summary
/// so its vector is stable across nightly sweeps.
/// </summary>
public static class InventorySummaryBuilder
{
    /// <summary>Hard cap on summary length; keeps embeddings well inside the model's window.</summary>
    public const int MaxLength = 8000;

    public static string Build(InventorySnapshot snapshot)
    {
        var sb = new StringBuilder();
        sb.Append("server: ").AppendLine(snapshot.Server);

        if (snapshot.Services is { } services)
        {
            var running = services.Where(s => string.Equals(s.Status, "Running", StringComparison.OrdinalIgnoreCase)).ToList();
            sb.Append("services: ").Append(running.Count).Append(" running of ").Append(services.Count).AppendLine(" total");
            AppendList(sb, "running services", running.Select(s => s.DisplayName ?? s.Name).Order(StringComparer.OrdinalIgnoreCase), 120);
        }

        if (snapshot.Software is { } software)
        {
            sb.Append("installed software: ").Append(software.Count).AppendLine(" entries");
            AppendList(sb, "software", software.Select(s => s.Version is null ? s.Name : $"{s.Name} {s.Version}"), 200);
        }

        if (snapshot.Ports is { } ports)
            // Sorted here too (not just in the collector): the builder's contract is that an
            // unchanged server yields an identical summary regardless of input order.
            AppendList(sb, "listening ports", ports
                .OrderBy(p => p.Port).ThenBy(p => p.Address, StringComparer.Ordinal).ThenBy(p => p.ProcessName, StringComparer.Ordinal)
                .Select(p => p.ProcessName is null ? p.Port.ToString() : $"{p.Port} ({p.ProcessName})"), 100);

        if (snapshot.Gpo is { } gpo)
            AppendList(sb, "applied gpos", gpo.Select(g => g.Name), 100);

        if (snapshot.Hotfix is { } hotfix)
        {
            sb.Append("hotfixes: ").Append(hotfix.Count).AppendLine(" installed");
            // Tie-break by Id: many hotfixes share a Patch-Tuesday date, so date-only sorting
            // leaves the top-20 set at the mercy of WMI enumeration order and the summary
            // (hence the embedding vector) would drift for an unchanged server.
            AppendList(sb, "recent hotfixes",
                hotfix.OrderByDescending(h => h.InstalledOn ?? DateOnly.MinValue)
                    .ThenBy(h => h.Id, StringComparer.Ordinal)
                    .Take(20).Select(h => h.Id), 20);
        }

        if (snapshot.Certs is { } certs)
            AppendList(sb, "certificates", certs.Select(c =>
                    c.NotAfter is null ? c.Subject ?? c.Thumbprint : $"{c.Subject ?? c.Thumbprint} (expires {c.NotAfter:yyyy-MM-dd})")
                .Order(StringComparer.OrdinalIgnoreCase), 50);

        if (snapshot.Tasks is { } tasks)
            AppendList(sb, "scheduled tasks", tasks.Select(t => t.Name).Order(StringComparer.OrdinalIgnoreCase), 100);

        return TextUtil.Truncate(sb.ToString(), MaxLength);
    }

    private static void AppendList(StringBuilder sb, string label, IEnumerable<string> items, int cap)
    {
        var list = items.Where(i => !string.IsNullOrWhiteSpace(i)).Take(cap).ToList();
        if (list.Count == 0)
            return;

        sb.Append(label).Append(": ").AppendLine(string.Join("; ", list));
    }
}
