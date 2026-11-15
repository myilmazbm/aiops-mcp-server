namespace AiOps.Mcp.Tests;

/// <summary>
/// Guards the environment policy the owner set for this fleet: Windows Firewall is managed
/// on network appliances, so no deployment script may create or modify a host firewall rule.
/// The trap is that the cmdlets which do it are not named "firewall" — Enable-PSRemoting and
/// Set-WSManQuickConfig both create WinRM firewall rules as a side effect, which is exactly
/// how the violation got in the first time.
/// </summary>
public class DeploymentPolicyTests
{
    private static readonly string[] FirewallTouchingCommands =
    [
        "New-NetFirewallRule", "Set-NetFirewallRule", "Remove-NetFirewallRule", "Enable-NetFirewallRule",
        "Disable-NetFirewallRule", "Set-NetFirewallProfile", "netsh advfirewall", "netsh firewall",
        // Side-effect offenders: these configure WinRM *and* open the firewall.
        "Enable-PSRemoting", "Set-WSManQuickConfig", "winrm quickconfig",
    ];

    public static TheoryData<string> DeploymentScripts()
    {
        var data = new TheoryData<string>();
        foreach (var path in System.IO.Directory.EnumerateFiles(LocateRepoDirectory("deploy"), "*.ps1", SearchOption.AllDirectories))
            data.Add(path);
        return data;
    }

    [Theory]
    [MemberData(nameof(DeploymentScripts))]
    public void No_deployment_script_touches_the_windows_firewall(string scriptPath)
    {
        var lines = File.ReadAllLines(scriptPath);
        for (var i = 0; i < lines.Length; i++)
        {
            // Comments may name these commands to explain why they are banned.
            var code = lines[i].TrimStart();
            if (code.StartsWith('#'))
                continue;

            foreach (var command in FirewallTouchingCommands)
            {
                Assert.False(
                    code.Contains(command, StringComparison.OrdinalIgnoreCase),
                    $"{Path.GetFileName(scriptPath)}:{i + 1} uses '{command}', which creates or changes a Windows " +
                    "Firewall rule. Firewall is managed at the network layer for this fleet; required flows are " +
                    "documented for the network team instead.");
            }
        }
    }

    [Fact]
    public void Deployment_scripts_exist_so_the_scan_is_not_vacuously_green()
    {
        // A refactor that moves or renames deploy/ must not silently turn the guard above
        // into a test over zero files.
        Assert.NotEmpty(System.IO.Directory.EnumerateFiles(LocateRepoDirectory("deploy"), "*.ps1", SearchOption.AllDirectories));
    }

    private static string LocateRepoDirectory(string relativePath)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, relativePath);
            if (System.IO.Directory.Exists(candidate))
                return candidate;
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException($"Could not locate {relativePath} walking up from {AppContext.BaseDirectory}");
    }

    [Fact]
    public void The_sccm_detection_hash_matches_the_role_capability_it_guards()
    {
        // Detect-AIOpsReader.ps1 carries the expected SHA256 of AIOpsReader.psrc so the SCCM
        // baseline redeploys the role when its content changes — the security boundary must be
        // able to move across 500 servers. A stale constant is invisible until it is deployed:
        // every server then reports NonCompliant on every evaluation cycle forever.
        var psrc = LocateRepoFile(@"deploy\jea\AIOpsReader.psrc");
        var detect = File.ReadAllText(LocateRepoFile(@"deploy\sccm\Detect-AIOpsReader.ps1"));

        var actual = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(psrc)));
        var embedded = System.Text.RegularExpressions.Regex.Matches(detect, "[0-9A-Fa-f]{64}")
            .Select(m => m.Value)
            .ToList();

        Assert.NotEmpty(embedded);
        Assert.All(embedded, hash => Assert.Equal(actual, hash, ignoreCase: true));
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
