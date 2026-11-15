using System.Collections;
using System.Management.Automation;
using AiOps.Mcp.Remoting;

namespace AiOps.Mcp.Tests;

/// <summary>
/// Pins the code against the JEA role it talks to. A JEA entry that carries a parameter
/// allow-list is published only under its module-qualified name, so a drift between the two
/// does not fail a build or a unit test — it fails on all ~500 servers at once, at runtime,
/// as "the term ... is not recognized".
/// </summary>
public class FleetCommandsTests
{
    private static Hashtable LoadFleetRole()
    {
        using var ps = PowerShell.Create();
        ps.AddScript(File.ReadAllText(LocateRepoFile(@"deploy\jea\AIOpsReader.psrc")));
        var result = ps.Invoke();
        Assert.False(ps.HadErrors, string.Join("; ", ps.Streams.Error.Select(e => e.ToString())));
        return (Hashtable)result.Single().BaseObject;
    }

    private static IEnumerable<string> RoleCmdletNames(Hashtable psrc)
        => ((IEnumerable)psrc["VisibleCmdlets"]!).Cast<object>()
            .Select(entry => entry switch
            {
                string s => s,
                Hashtable h => (string)h["Name"]!,
                _ => throw new InvalidOperationException($"Unexpected VisibleCmdlets entry: {entry}"),
            });

    [Fact]
    public void Every_command_the_server_sends_exists_in_the_fleet_role()
    {
        var inRole = RoleCmdletNames(LoadFleetRole()).ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.All(FleetCommands.All, name => Assert.Contains(name, inRole));
    }

    [Fact]
    public void The_role_exposes_nothing_the_server_does_not_use()
    {
        // Not a style rule: an entry nobody sends is unreviewed attack surface on 500 servers.
        var inRole = RoleCmdletNames(LoadFleetRole()).ToList();
        var used = FleetCommands.All.ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.All(inRole, name => Assert.Contains(name, used));
    }

    [Fact]
    public void Every_command_name_is_module_qualified()
    {
        // A parameter-restricted JEA entry has no unqualified alias, and qualification is what
        // stops a same-named cmdlet from a foreign module on the target from shadowing it.
        Assert.All(FleetCommands.All, name => Assert.Contains(@"\", name, StringComparison.Ordinal));
    }

    [Fact]
    public void Modules_that_need_importing_for_a_proxy_are_declared()
    {
        // JEA can only build the restricting proxy function when the defining module is in the
        // session. NetTCPIP and ScheduledTasks are CDXML modules and CimCmdlets is not loaded by
        // a RestrictedRemoteServer session, so without ModulesToImport their entries silently
        // produce no visible command — verified live before this test was written.
        var psrc = LoadFleetRole();
        var imported = ((IEnumerable)psrc["ModulesToImport"]!).Cast<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var module in FleetCommands.All
                     .Select(name => name.Split(@"\", StringSplitOptions.None)[0])
                     .Where(m => !m.StartsWith("Microsoft.PowerShell.", StringComparison.OrdinalIgnoreCase))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            Assert.Contains(module, imported);
        }
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
