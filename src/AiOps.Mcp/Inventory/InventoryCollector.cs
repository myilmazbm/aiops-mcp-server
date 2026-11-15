using System.Globalization;
using System.Management.Automation;
using AiOps.Contracts;
using AiOps.Mcp.Remoting;

namespace AiOps.Mcp.Inventory;

/// <summary>
/// Collects inventory sections from a server over the JEA endpoint. Used by both the
/// get_inventory tool and the nightly sweep. Every section enforces the hard row cap.
/// </summary>
public sealed class InventoryCollector(IRemoteExecutor executor)
{
    public static readonly string[] AllSections = ["services", "software", "ports", "gpo", "hotfix", "certs", "tasks"];

    /// <summary>Hard cap per section; keeps a snapshot near the ~100 KB budget. Not configurable by callers.</summary>
    public const int MaxRowsPerSection = 500;

    private const string UninstallKey64 = @"HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*";
    private const string UninstallKey32 = @"HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*";
    private const string GpoHistoryKey = @"HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Group Policy\History\*\*";

    public sealed record SectionOutcome(string Section, string? Error, bool Truncated, bool Unreachable);

    public sealed record CollectResult(InventorySnapshot Snapshot, IReadOnlyList<SectionOutcome> Outcomes);

    public async Task<CollectResult> CollectAsync(
        string server, IReadOnlyList<string> sections, CancellationToken cancellationToken)
    {
        var outcomes = new List<SectionOutcome>();
        var snapshot = new InventorySnapshot { Server = server, CollectedAt = DateTimeOffset.UtcNow };

        foreach (var section in sections)
        {
            var canonical = section.ToUpperInvariant();
            switch (canonical)
            {
                case "SERVICES":
                {
                    var (items, outcome) = await CollectSectionAsync(server, "services",
                        RemoteCommand.Create(FleetCommands.GetService), MapService, cancellationToken).ConfigureAwait(false);
                    snapshot = snapshot with { Services = items };
                    outcomes.Add(outcome);
                    break;
                }
                case "SOFTWARE":
                {
                    var (items, outcome) = await CollectSectionAsync(server, "software",
                        RemoteCommand.Create(FleetCommands.GetItemProperty, ("Path", new[] { UninstallKey64, UninstallKey32 })),
                        MapSoftware, cancellationToken).ConfigureAwait(false);
                    snapshot = snapshot with { Software = items?.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList() };
                    outcomes.Add(outcome);
                    break;
                }
                case "PORTS":
                {
                    var (items, outcome) = await CollectPortsAsync(server, cancellationToken).ConfigureAwait(false);
                    snapshot = snapshot with { Ports = items };
                    outcomes.Add(outcome);
                    break;
                }
                case "GPO":
                {
                    // Only the needed values are read: enumerating all values under the GP
                    // History keys hits an InvalidCastException on some value types
                    // (observed on Server 2025), failing the whole call.
                    var (items, outcome) = await CollectSectionAsync(server, "gpo",
                        RemoteCommand.Create(FleetCommands.GetItemProperty,
                            ("Path", GpoHistoryKey),
                            ("Name", new[] { "DisplayName", "GPOName" })),
                        MapGpo, cancellationToken).ConfigureAwait(false);
                    snapshot = snapshot with
                    {
                        Gpo = items?
                            .Where(g => !string.IsNullOrEmpty(g.Name))
                            .DistinctBy(g => g.Id ?? g.Name, StringComparer.OrdinalIgnoreCase)
                            .OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
                            .ToList(),
                    };
                    outcomes.Add(outcome);
                    break;
                }
                case "HOTFIX":
                {
                    var (items, outcome) = await CollectSectionAsync(server, "hotfix",
                        RemoteCommand.Create(FleetCommands.GetHotFix), MapHotfix, cancellationToken).ConfigureAwait(false);
                    snapshot = snapshot with { Hotfix = items };
                    outcomes.Add(outcome);
                    break;
                }
                case "CERTS":
                {
                    var (items, outcome) = await CollectSectionAsync(server, "certs",
                        RemoteCommand.Create(FleetCommands.GetChildItem, ("Path", @"Cert:\LocalMachine\My")),
                        MapCertificate, cancellationToken).ConfigureAwait(false);
                    snapshot = snapshot with { Certs = items };
                    outcomes.Add(outcome);
                    break;
                }
                case "TASKS":
                {
                    var (items, outcome) = await CollectSectionAsync(server, "tasks",
                        RemoteCommand.Create(FleetCommands.GetScheduledTask), MapScheduledTask, cancellationToken).ConfigureAwait(false);
                    snapshot = snapshot with { Tasks = items };
                    outcomes.Add(outcome);
                    break;
                }
                default:
                    throw new ArgumentException($"Unknown inventory section '{section}'.", nameof(sections));
            }

            // A dead server fails every remaining section the same way; stop early.
            if (outcomes[^1].Unreachable)
                break;
        }

        return new CollectResult(snapshot, outcomes);
    }

    private async Task<(List<T>? Items, SectionOutcome Outcome)> CollectSectionAsync<T>(
        string server, string section, RemoteCommand command, Func<PSObject, T?> map, CancellationToken cancellationToken)
        where T : class
    {
        var call = await executor.InvokeAsync(server, command, cancellationToken).ConfigureAwait(false);

        if (call.Status == ServerCallStatus.Unreachable)
            return (null, new SectionOutcome(section, call.Error ?? "unreachable", Truncated: false, Unreachable: true));

        // Partial output with errors is normal (e.g. a missing WOW6432Node hive); only a
        // completely empty failed call counts as a section failure.
        if (call.Status != ServerCallStatus.Ok && call.Output.Count == 0)
            return (null, new SectionOutcome(section, call.Error ?? "failed", Truncated: false, Unreachable: false));

        var items = call.Output.Select(map).Where(i => i is not null).Select(i => i!).ToList();
        var truncated = items.Count > MaxRowsPerSection;
        if (truncated)
            items = items.Take(MaxRowsPerSection).ToList();

        return (items, new SectionOutcome(section, Error: null, truncated, Unreachable: false));
    }

    private async Task<(List<ListeningPortDto>? Items, SectionOutcome Outcome)> CollectPortsAsync(
        string server, CancellationToken cancellationToken)
    {
        var (ports, outcome) = await CollectSectionAsync(server, "ports",
            RemoteCommand.Create(FleetCommands.GetNetTcpConnection, ("State", "Listen")),
            MapPort, cancellationToken).ConfigureAwait(false);

        if (ports is not { Count: > 0 })
            return (ports, outcome);

        // Never parse netstat: owning process names come from Get-Process matched by PID.
        // Name resolution is best-effort; ports are still returned if it fails.
        var processCall = await executor.InvokeAsync(
            server, RemoteCommand.Create(FleetCommands.GetProcess), cancellationToken).ConfigureAwait(false);

        // Partial output alongside errors is still usable; only an unreachable/empty call is skipped.
        if (processCall.Status != ServerCallStatus.Unreachable && processCall.Output.Count > 0)
        {
            var names = new Dictionary<int, string>();
            foreach (var process in processCall.Output)
            {
                var pid = PsValue.Get<int>(process, "Id");
                var name = PsValue.Get<string>(process, "ProcessName");
                if (name is not null)
                    names[pid] = name;
            }

            ports = ports
                .Select(p => p.Pid is int pid && names.TryGetValue(pid, out var name) ? p with { ProcessName = name } : p)
                .ToList();
        }

        // Full-key ordering (not just Port): a stable order keeps the derived inventory
        // summary — and thus the embedding vector — identical for an unchanged server.
        var deduped = ports
            .DistinctBy(p => (p.Address, p.Port, p.Pid))
            .OrderBy(p => p.Port)
            .ThenBy(p => p.Address, StringComparer.Ordinal)
            .ThenBy(p => p.Pid ?? 0)
            .ToList();

        return (deduped, outcome);
    }

    private static ServiceDto? MapService(PSObject obj)
    {
        var name = PsValue.Get<string>(obj, "Name");
        if (string.IsNullOrEmpty(name))
            return null;

        return new ServiceDto
        {
            Name = name,
            DisplayName = PsValue.Get<string>(obj, "DisplayName"),
            Status = PsValue.Get<string>(obj, "Status"),
            StartType = PsValue.Get<string>(obj, "StartType"),
        };
    }

    private static SoftwareDto? MapSoftware(PSObject obj)
    {
        // Uninstall subkeys without a DisplayName are patches/plumbing, not installed programs.
        var name = PsValue.Get<string>(obj, "DisplayName");
        if (string.IsNullOrWhiteSpace(name))
            return null;

        return new SoftwareDto
        {
            Name = name,
            Version = PsValue.Get<string>(obj, "DisplayVersion"),
            Publisher = PsValue.Get<string>(obj, "Publisher"),
            InstallDate = ParseInstallDate(PsValue.Get<string>(obj, "InstallDate")),
        };
    }

    /// <summary>Registry InstallDate is a plain "yyyyMMdd" string when present, and garbage often enough.</summary>
    internal static DateOnly? ParseInstallDate(string? raw)
        => DateOnly.TryParseExact(raw, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : null;

    private static ListeningPortDto? MapPort(PSObject obj)
    {
        var port = PsValue.Get<int>(obj, "LocalPort");
        var address = PsValue.Get<string>(obj, "LocalAddress");
        if (port == 0 || address is null)
            return null;

        var pid = PsValue.Get<int>(obj, "OwningProcess");
        return new ListeningPortDto
        {
            Port = port,
            Address = address,
            Pid = pid == 0 ? null : pid,
        };
    }

    private static GpoDto? MapGpo(PSObject obj)
    {
        var name = PsValue.Get<string>(obj, "DisplayName");
        if (string.IsNullOrWhiteSpace(name))
            return null;

        // GPOName holds the policy GUID in the GP History keys (there is no GPOID value).
        return new GpoDto { Name = name, Id = PsValue.Get<string>(obj, "GPOName") };
    }

    private static HotfixDto? MapHotfix(PSObject obj)
    {
        var id = PsValue.Get<string>(obj, "HotFixID");
        if (string.IsNullOrEmpty(id))
            return null;

        DateOnly? installedOn = null;
        var rawDate = PsValue.Get<DateTime>(obj, "InstalledOn");
        if (rawDate != default)
            installedOn = DateOnly.FromDateTime(rawDate);

        return new HotfixDto
        {
            Id = id,
            Description = PsValue.Get<string>(obj, "Description"),
            InstalledOn = installedOn,
        };
    }

    private static CertificateDto? MapCertificate(PSObject obj)
    {
        var thumbprint = PsValue.Get<string>(obj, "Thumbprint");
        if (string.IsNullOrEmpty(thumbprint))
            return null;

        return new CertificateDto
        {
            Thumbprint = thumbprint,
            Subject = PsValue.Get<string>(obj, "Subject"),
            Issuer = PsValue.Get<string>(obj, "Issuer"),
            NotAfter = ToSafeOffset(PsValue.Get<DateTime>(obj, "NotAfter")),
        };
    }

    /// <summary>
    /// Applying a local offset to extreme dates (year 1 or 9999 — e.g. never-expiring
    /// self-signed certs) over/underflows DateTimeOffset; pin those to UTC instead.
    /// </summary>
    internal static DateTimeOffset? ToSafeOffset(DateTime value)
    {
        if (value == default)
            return null;

        return value.Year is <= 1 or >= 9999
            ? new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc))
            : value;
    }

    private static ScheduledTaskDto? MapScheduledTask(PSObject obj)
    {
        var name = PsValue.Get<string>(obj, "TaskName");
        if (string.IsNullOrEmpty(name))
            return null;

        return new ScheduledTaskDto
        {
            Name = name,
            Path = PsValue.Get<string>(obj, "TaskPath"),
            State = PsValue.Get<string>(obj, "State"),
        };
    }
}
