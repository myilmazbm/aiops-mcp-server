namespace AiOps.Mcp.Remoting;

/// <summary>
/// The module-qualified command names the fleet JEA role exposes.
///
/// Qualification is not cosmetic. A JEA entry that carries a parameter allow-list is
/// published as a proxy function named "Module\Cmdlet" and the unqualified alias does not
/// exist, so an unqualified name fails with "is not recognized" on every fleet server.
/// Qualification is also what stops a same-named cmdlet from a foreign module on the
/// target's PSModulePath from shadowing the intended one.
///
/// Every name here must match an entry in deploy/jea/AIOpsReader.psrc exactly;
/// FleetCommandsTests pins that.
/// </summary>
public static class FleetCommands
{
    private const string Management = @"Microsoft.PowerShell.Management\";
    private const string Diagnostics = @"Microsoft.PowerShell.Diagnostics\";
    private const string Utility = @"Microsoft.PowerShell.Utility\";

    public const string GetWinEvent = Diagnostics + "Get-WinEvent";
    public const string GetCounter = Diagnostics + "Get-Counter";

    public const string GetService = Management + "Get-Service";
    public const string GetProcess = Management + "Get-Process";
    public const string GetHotFix = Management + "Get-HotFix";
    public const string GetItem = Management + "Get-Item";
    public const string GetItemProperty = Management + "Get-ItemProperty";
    public const string GetChildItem = Management + "Get-ChildItem";

    public const string GetNetTcpConnection = @"NetTCPIP\Get-NetTCPConnection";
    public const string GetScheduledTask = @"ScheduledTasks\Get-ScheduledTask";
    public const string GetScheduledTaskInfo = @"ScheduledTasks\Get-ScheduledTaskInfo";

    public const string SelectObject = Utility + "Select-Object";
    public const string GetCimInstance = @"CimCmdlets\Get-CimInstance";

    /// <summary>Every command name this server sends to the fleet endpoint.</summary>
    public static readonly IReadOnlyList<string> All =
    [
        GetWinEvent, GetCounter, GetService, GetProcess, GetHotFix, GetItem, GetItemProperty,
        GetChildItem, GetNetTcpConnection, GetScheduledTask, GetScheduledTaskInfo, SelectObject,
        GetCimInstance,
    ];
}
