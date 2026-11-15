using System.Collections;
using System.Management.Automation;

namespace AiOps.Mcp.Tests;

/// <summary>
/// Regression guard over the JEA role capabilities (deploy/jea/**/*.psrc) — the security
/// boundary of the system. Any change to VisibleCmdlets is an approval-required security
/// decision; these tests pin the approved shape so an unreviewed addition fails CI.
/// </summary>
public class JeaRoleCapabilityTests
{
    private const string FleetRole = @"deploy\jea\AIOpsReader.psrc";
    private const string ScomRole = @"deploy\jea\scom\AIOpsScomReader.psrc";

    private static readonly string[] ForbiddenVerbs =
        ["Set-", "New-", "Remove-", "Add-", "Clear-", "Start-", "Stop-", "Invoke-", "Enter-", "Register-",
         "Unregister-", "Resolve-", "Update-", "Import-", "Export-", "Enable-", "Disable-", "Restart-"];

    // A role capability (.psrc) may contain script blocks in FunctionDefinitions, which the
    // safe Import-PowerShellDataFile loader rejects (as JEA itself loads them specially).
    // Execute the file — a hashtable literal — to get the table with its script blocks intact.
    private static Hashtable LoadPsrc(string relativePath)
    {
        using var ps = PowerShell.Create();
        ps.AddScript(File.ReadAllText(LocateRepoFile(relativePath)));
        var result = ps.Invoke();
        Assert.False(ps.HadErrors, string.Join("; ", ps.Streams.Error.Select(e => e.ToString())));
        return (Hashtable)result.Single().BaseObject;
    }

    // A session configuration (.pssc) has no script blocks; the safe loader is fine.
    private static Hashtable ImportPsd1(string path)
    {
        using var ps = PowerShell.Create();
        ps.AddCommand("Import-PowerShellDataFile").AddParameter("Path", path);
        var result = ps.Invoke<Hashtable>();
        Assert.False(ps.HadErrors, string.Join("; ", ps.Streams.Error.Select(e => e.ToString())));
        return result.Single();
    }

    private static IEnumerable<object> VisibleCmdlets(Hashtable psrc)
        => ((IEnumerable)psrc["VisibleCmdlets"]!).Cast<object>();

    /// <summary>A cmdlet entry is either a plain name string or a hashtable with a Name key.</summary>
    private static string CmdletName(object entry)
        => entry switch
        {
            string s => s,
            Hashtable h => (string)h["Name"]!,
            _ => throw new InvalidOperationException($"Unexpected VisibleCmdlets entry: {entry}"),
        };

    [Fact]
    public void Fleet_role_visible_cmdlets_are_module_qualified()
    {
        // Unqualified names resolve by name against the whole PSModulePath and can be shadowed
        // by a foreign same-named module; every entry must pin its trusted module.
        foreach (var entry in VisibleCmdlets(LoadPsrc(FleetRole)))
            Assert.Contains('\\', CmdletName(entry));
    }

    [Fact]
    public void Fleet_role_has_no_state_changing_cmdlet()
    {
        foreach (var entry in VisibleCmdlets(LoadPsrc(FleetRole)))
        {
            var leaf = CmdletName(entry).Split('\\')[^1];
            Assert.DoesNotContain(ForbiddenVerbs, verb => leaf.StartsWith(verb, StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void Scom_role_exposes_only_read_only_wrapper_functions()
    {
        var psrc = LoadPsrc(ScomRole);

        // No raw cmdlets are visible to the caller — only the trusted Get-* wrapper functions.
        Assert.False(psrc.ContainsKey("VisibleCmdlets"), "SCOM role must not expose VisibleCmdlets");

        var visible = ((IEnumerable)psrc["VisibleFunctions"]!).Cast<string>().OrderBy(x => x).ToList();
        Assert.Equal(
            [
                "Get-AIOpsScomAlertDetail", "Get-AIOpsScomAlerts", "Get-AIOpsScomHealth",
                "Get-AIOpsScomInventory", "Get-AIOpsScomMonitoringHealth", "Get-AIOpsScomPerformance",
                "Get-AIOpsScomStateChanges",
            ],
            visible);

        // Every visible function is defined here (no external function is exposed) and reads.
        var defined = ((IEnumerable)psrc["FunctionDefinitions"]!).Cast<Hashtable>()
            .Select(h => (string)h["Name"]!).ToList();
        Assert.All(visible, v => Assert.Contains(v, defined));
        Assert.All(defined, name =>
            Assert.DoesNotContain(ForbiddenVerbs, verb => name.StartsWith(verb, StringComparison.OrdinalIgnoreCase)));
        Assert.All(defined, name => Assert.StartsWith("Get-", name, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Scom_role_function_bodies_contain_no_write_operations()
    {
        // The wrapper bodies are trusted code running in Full Language mode, so their content
        // is itself part of the security boundary: they must only read.
        var psrc = LoadPsrc(ScomRole);
        foreach (var fn in ((IEnumerable)psrc["FunctionDefinitions"]!).Cast<Hashtable>())
        {
            var body = fn["ScriptBlock"]!.ToString() ?? "";
            Assert.DoesNotContain(
                ForbiddenVerbs.Where(v => !v.Equals("Import-", StringComparison.OrdinalIgnoreCase)),
                verb => body.Contains(verb, StringComparison.OrdinalIgnoreCase));
            // Import-Module OperationsManager is the one permitted import; nothing else.
            foreach (var line in body.Split('\n').Where(l => l.Contains("Import-", StringComparison.OrdinalIgnoreCase)))
                Assert.Contains("Import-Module OperationsManager", line, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Fleet_role_select_object_is_projection_only_never_calculated_properties()
    {
        // The fleet role restricts Select-Object's parameters (defense in depth). The SCOM
        // role cannot: the OperationsManager module calls Select-Object during import, so a
        // restricted proxy breaks module load — NoLanguage neutralizes the -Property risk there.
        var entry = VisibleCmdlets(LoadPsrc(FleetRole))
            .OfType<Hashtable>()
            .Single(h => ((string)h["Name"]!).EndsWith("Select-Object", StringComparison.OrdinalIgnoreCase));

        var paramNames = ((IEnumerable)entry["Parameters"]!)
            .Cast<Hashtable>()
            .Select(p => (string)p["Name"]!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.DoesNotContain("Property", paramNames);
        Assert.Subset(
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "InputObject", "ExpandProperty", "First", "Last" },
            paramNames);
    }

    [Fact]
    public void Scom_session_is_locked_down_by_no_language_plus_the_function_allowlist()
    {
        // SessionType is Default, NOT RestrictedRemoteServer: the restricted type installs
        // proxy functions that shadow real cmdlets and break the OperationsManager module.
        // What locks the session down instead is NoLanguage (no scripts, no variables) plus
        // the VisibleFunctions allowlist — verified live: Get-ChildItem, Remove-Item, raw
        // Get-SCOMAlert and even Get-Command are all refused.
        var pssc = ImportPsd1(LocateRepoFile(@"deploy\jea\scom\AIOpsScomReader.pssc"));
        Assert.Equal("NoLanguage", (string)pssc["LanguageMode"]!);
        Assert.Equal("Default", (string)pssc["SessionType"]!);
        Assert.NotNull(pssc["TranscriptDirectory"]);
        Assert.NotNull(pssc["RoleDefinitions"]);
    }

    [Fact]
    public void Get_ciminstance_is_class_restricted_and_excludes_win32_product()
    {
        var entry = VisibleCmdlets(LoadPsrc(FleetRole))
            .OfType<Hashtable>()
            .Single(h => ((string)h["Name"]!).EndsWith("Get-CimInstance", StringComparison.OrdinalIgnoreCase));

        var classNameParam = ((IEnumerable)entry["Parameters"]!)
            .Cast<Hashtable>()
            .Single(p => string.Equals((string)p["Name"]!, "ClassName", StringComparison.OrdinalIgnoreCase));

        var allowedClasses = ((IEnumerable)classNameParam["ValidateSet"]!).Cast<string>().ToList();
        Assert.NotEmpty(allowedClasses);
        // Win32_Product triggers MSI reconfigure; it must never be queryable (CLAUDE.md pitfall).
        Assert.DoesNotContain("Win32_Product", allowedClasses);
    }

    [Fact]
    public void Scom_wrapper_bodies_define_no_function_that_outlives_the_call()
    {
        // The SCOM endpoint's lockdown is NoLanguage plus the VisibleFunctions allow-list,
        // with no RestrictedRemoteServer backstop. JEA stamps command visibility when the
        // session is built, so a function created at runtime in script or global scope is
        // outside that allow-list — and the session pool keeps the runspace alive, so it
        // persists for every later call on that server.
        foreach (var fn in ((IEnumerable)LoadPsrc(ScomRole)["FunctionDefinitions"]!).Cast<Hashtable>())
        {
            var body = fn["ScriptBlock"]!.ToString() ?? "";
            Assert.DoesNotContain("function script:", body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("function global:", body, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Monitoring_health_asks_for_the_role_instead_of_probing_administrative_calls()
    {
        // The service account is a Read-Only Operator. Each administrative SDK call it makes is
        // refused AND logged by SCOM as an error event (26319) on the management server, so
        // "try it and catch the refusal" turned every invocation into six error events in the
        // very log the tool exists to help read. The role must be asked for up front.
        var body = ((IEnumerable)LoadPsrc(ScomRole)["FunctionDefinitions"]!).Cast<Hashtable>()
            .Single(f => (string)f["Name"]! == "Get-AIOpsScomMonitoringHealth")["ScriptBlock"]!
            .ToString() ?? "";

        Assert.Contains("IsUserAdministrator()", body, StringComparison.Ordinal);
        Assert.Contains("if (-not $isAdmin)", body, StringComparison.Ordinal);

        // No administrative read may run outside the guarded helper.
        string[] adminCmdlets =
        [
            "Get-SCOMManagementServer", "Get-SCOMAgent", "Get-SCOMHeartbeatSetting",
            "Get-SCOMDatabaseGroomingSetting", "Get-SCOMPendingManagement", "Get-SCOMNotificationSubscription",
        ];
        foreach (var line in body.Split('\n').Where(l => !l.TrimStart().StartsWith('#')))
        {
            foreach (var cmdlet in adminCmdlets.Where(c => line.Contains(c, StringComparison.Ordinal)))
                Assert.True(line.Contains("& $tryAdmin", StringComparison.Ordinal), $"{cmdlet} is called outside the role guard: {line.Trim()}");
        }
    }

    [Fact]
    public void Grey_agent_detection_means_not_reporting_not_merely_unhealthy()
    {
        // A grey agent is one SCOM has stopped hearing from: the health service watcher is
        // Uninitialized or unavailable. Selecting "anything other than Success" also swept up
        // agents that report perfectly well but have a monitor in Warning, and the contract
        // tells the LLM those hosts are unmonitored — which makes it discard every other SCOM
        // answer about a healthy server.
        var body = ((IEnumerable)LoadPsrc(ScomRole)["FunctionDefinitions"]!).Cast<Hashtable>()
            .Single(f => (string)f["Name"]! == "Get-AIOpsScomMonitoringHealth")["ScriptBlock"]!
            .ToString() ?? "";

        var greyLine = body.Split('\n').Single(l => l.Contains("$grey =", StringComparison.Ordinal));
        Assert.Contains("Uninitialized", greyLine, StringComparison.Ordinal);
        Assert.Contains("IsAvailable", greyLine, StringComparison.Ordinal);
        Assert.DoesNotContain("-ne 'Success'", greyLine, StringComparison.Ordinal);
    }

    [Fact]
    public void Fleet_session_configuration_keeps_its_approved_lockdown()
    {
        // The file that ships to every fleet server had no test at all: flipping SessionType to
        // Default, LanguageMode to FullLanguage, or dropping the transcript directory or the
        // virtual account would all have shipped green. Unlike the SCOM role, this one CAN use
        // RestrictedRemoteServer, and CLAUDE.md requires that it does.
        var pssc = ImportPsd1(LocateRepoFile(@"deploy\jea\AIOpsReader.pssc"));

        Assert.Equal("RestrictedRemoteServer", (string)pssc["SessionType"]!);
        Assert.Equal("NoLanguage", (string)pssc["LanguageMode"]!);
        Assert.NotNull(pssc["TranscriptDirectory"]);
        Assert.NotNull(pssc["RoleDefinitions"]);
        Assert.True((bool)pssc["RunAsVirtualAccount"]!);
    }

    [Fact]
    public void Fleet_role_exposes_cmdlets_and_nothing_else()
    {
        // VisibleCmdlets was the only key any test looked at. A FunctionDefinitions block or a
        // VisibleExternalCommands entry pointing at cmd.exe would grant arbitrary execution on
        // every server while all the existing assertions still passed.
        var psrc = LoadPsrc(FleetRole);

        foreach (var key in new[]
                 { "VisibleFunctions", "VisibleExternalCommands", "VisibleAliases", "FunctionDefinitions", "ScriptsToProcess" })
        {
            Assert.False(psrc.ContainsKey(key), $"Fleet role must not define {key}");
        }

        var providers = ((IEnumerable)psrc["VisibleProviders"]!).Cast<string>().OrderBy(x => x).ToList();
        Assert.Equal(["Certificate", "Registry"], providers);
    }

    [Fact]
    public void Every_fleet_cmdlet_carries_a_parameter_allow_list()
    {
        // A bare string entry permits EVERY parameter of the cmdlet. That is how the role came
        // to allow arbitrary registry reads (-Path), raw Security log dumps (-LogName) and
        // execution against another host (-ComputerName / -CimSession).
        foreach (var entry in VisibleCmdlets(LoadPsrc(FleetRole)))
        {
            var table = Assert.IsType<Hashtable>(entry);
            Assert.True(
                table.ContainsKey("Parameters"),
                $"{table["Name"]} has no parameter allow-list, so every one of its parameters is permitted.");
        }
    }

    [Fact]
    public void No_fleet_cmdlet_can_be_redirected_to_another_host()
    {
        // Domain controllers are out of scope and the target host is the session, never an
        // argument; a second hop would also evade the per-server logging the MCP layer does.
        string[] redirecting = ["ComputerName", "CimSession", "Session", "AsJob"];

        foreach (var entry in VisibleCmdlets(LoadPsrc(FleetRole)).OfType<Hashtable>())
        {
            var names = ((IEnumerable)entry["Parameters"]!).Cast<Hashtable>().Select(p => (string)p["Name"]!);
            Assert.DoesNotContain(names, n => redirecting.Contains(n, StringComparer.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void Registry_and_file_paths_are_pattern_restricted()
    {
        // The virtual account is a local administrator, so an unrestricted -Path reads the
        // Winlogon autologon password, service ImagePath values and SNMP community strings.
        foreach (var entry in VisibleCmdlets(LoadPsrc(FleetRole)).OfType<Hashtable>())
        {
            var path = ((IEnumerable)entry["Parameters"]!).Cast<Hashtable>()
                .FirstOrDefault(p => string.Equals((string)p["Name"]!, "Path", StringComparison.OrdinalIgnoreCase));
            if (path is null)
                continue;

            var pattern = Assert.IsType<string>(path["ValidatePattern"]);
            Assert.StartsWith("^", pattern, StringComparison.Ordinal);
            Assert.EndsWith("$", pattern, StringComparison.Ordinal);
            Assert.DoesNotContain("Winlogon", pattern, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("CurrentControlSet", pattern, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Get_winevent_takes_a_server_side_filter_and_not_a_log_name()
    {
        // -LogName Security would bulk-read the security log, bypassing both the FilterXml
        // discipline (filter runs on the target) and the MCP layer's message truncation.
        var entry = VisibleCmdlets(LoadPsrc(FleetRole))
            .OfType<Hashtable>()
            .Single(h => ((string)h["Name"]!).EndsWith("Get-WinEvent", StringComparison.OrdinalIgnoreCase));

        var names = ((IEnumerable)entry["Parameters"]!).Cast<Hashtable>()
            .Select(p => (string)p["Name"]!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.Contains("FilterXml", names);
        Assert.DoesNotContain("LogName", names);
        Assert.DoesNotContain("Path", names);
    }

    private static string LocateRepoFile(string relativePath)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, relativePath);
            if (File.Exists(candidate))
                return candidate;
            dir = dir.Parent;
        }

        throw new FileNotFoundException($"Could not locate {relativePath} walking up from {AppContext.BaseDirectory}");
    }
}
