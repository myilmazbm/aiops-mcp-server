using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Management.Automation;
using System.Text.Json;
using AiOps.Contracts;
using AiOps.Mcp.Remoting;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace AiOps.Mcp.Tools.RunReadonly;

[McpServerToolType]
public static class RunReadonlyTool
{
    /// <summary>Hard output cap; mandatory and not exposed as a parameter.</summary>
    public const int MaxRows = 200;
    public const int MaxValueLength = 500;
    public const int MaxPropertiesPerRow = 64;

    [McpServerTool(Name = "run_readonly", ReadOnly = true)]
    [Description("Runs a single cmdlet on a server's JEA endpoint (AIOpsReader). Cmdlet names must be module-qualified, e.g. 'Microsoft.PowerShell.Management\\Get-Service'. Available: Microsoft.PowerShell.Management\\{Get-Service, Get-Process, Get-HotFix, Get-Item, Get-ItemProperty, Get-ChildItem}, Microsoft.PowerShell.Diagnostics\\{Get-WinEvent, Get-Counter}, NetTCPIP\\Get-NetTCPConnection, ScheduledTasks\\{Get-ScheduledTask, Get-ScheduledTaskInfo}, CimCmdlets\\Get-CimInstance. The endpoint's role capability decides what is allowed; anything outside its read-only allow-list, including most parameters, is rejected server-side. Registry paths and CIM classes are restricted to a fixed allow-list, and Get-WinEvent takes FilterXml rather than LogName. Output is capped at 200 rows, values at 500 characters.")]
    public static async Task<RunReadonlyResult> RunReadonly(
        IRemoteExecutor executor,
        ILoggerFactory loggerFactory,
        [Description("Target server name (FQDN)")] string server,
        [Description(@"Module-qualified cmdlet name, e.g. 'Microsoft.PowerShell.Management\Get-Service'")] string command,
        [Description("Cmdlet parameters as a flat JSON object, e.g. {\"Name\":\"WinRM\"}. Values may be strings, numbers, booleans or arrays of those.")]
        Dictionary<string, JsonElement>? parameters = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(server))
            throw new McpException("server is required.");
        if (string.IsNullOrWhiteSpace(command))
            throw new McpException("command is required.");

        // Security boundary note: the command is passed through verbatim. What may run is
        // decided solely by the JEA role capability on the target — never by string
        // inspection here (see CLAUDE.md security invariant #1).
        //
        // The Select-Object -First backstop is appended structurally (not woven into the
        // command text), so the boundary is unaffected: it bounds output ON THE TARGET so a
        // caller cannot make the executor buffer an unbounded stream into MCP memory. +1 so
        // the existing row-count truncation detection still fires. Select-Object -First is
        // already in the JEA role capability.
        var remoteCommand = new RemoteCommand(command.Trim(), ConvertParameters(parameters))
            .PipeTo(FleetCommands.SelectObject, ("First", MaxRows + 1));

        var stopwatch = Stopwatch.StartNew();
        var call = await executor.InvokeAsync(server, remoteCommand, cancellationToken).ConfigureAwait(false);

        var truncated = call.Output.Count > MaxRows;
        var rows = call.Output.Take(MaxRows).Select(MapRow).ToList();

        loggerFactory.CreateLogger("AiOps.Mcp.Tools.run_readonly").LogInformation(
            "run_readonly server={Server} command={Command} parameters={Parameters} status={Status} rows={RowCount} took {ElapsedMs} ms",
            server, remoteCommand.CommandName, string.Join(",", remoteCommand.Parameters.Keys),
            call.Status, rows.Count, stopwatch.ElapsedMilliseconds);

        return new RunReadonlyResult
        {
            Server = server,
            Status = call.Status.ToWire(),
            Error = call.Error,
            ErrorId = call.ErrorId,
            Truncated = truncated,
            Rows = rows,
        };
    }

    internal static IReadOnlyDictionary<string, object?> ConvertParameters(Dictionary<string, JsonElement>? parameters)
    {
        if (parameters is null)
            return new Dictionary<string, object?>();

        return parameters.ToDictionary(p => p.Key, p => ConvertValue(p.Key, p.Value, allowArray: true));
    }

    private static object? ConvertValue(string name, JsonElement value, bool allowArray)
        => value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            // Explicit boxing: the ternary would otherwise unify long|double to double,
            // silently turning every integer parameter into a floating-point value.
            JsonValueKind.Number => value.TryGetInt64(out var l) ? (object)l : value.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Array when allowArray =>
                value.EnumerateArray().Select(e => ConvertValue(name, e, allowArray: false)).ToArray(),
            // A JSON null is not a PowerShell $null bind — the executor drops null values, so
            // the cmdlet would silently run without the parameter (a different query than
            // asked) while the audit log still lists the key. Reject it explicitly, like
            // nested objects, rather than coerce it.
            _ => throw new McpException(
                $"Parameter '{name}' has unsupported value kind {value.ValueKind}; use strings, numbers, booleans or flat arrays (no null, no nested objects)."),
        };

    private static IReadOnlyDictionary<string, string?> MapRow(PSObject obj)
    {
        // Primitive pipeline output (a string, number, date…) has no useful note
        // properties; represent it as a single value.
        if (obj.BaseObject is string or ValueType)
            return new Dictionary<string, string?> { ["value"] = FormatValue(obj.BaseObject) };

        var row = new Dictionary<string, string?>();
        foreach (var property in obj.Properties)
        {
            if (row.Count >= MaxPropertiesPerRow)
                break;

            object? value;
            try
            {
                value = property.Value;
            }
            catch (Exception)
            {
                continue; // some adapter properties throw on access; skip them
            }

            row[property.Name] = FormatValue(value);
        }

        return row;
    }

    private static string? FormatValue(object? value)
    {
        var text = value switch
        {
            null => null,
            string s => s,
            DateTime dt => dt.ToString("O", CultureInfo.InvariantCulture),
            DateTimeOffset dto => dto.ToString("O", CultureInfo.InvariantCulture),
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            // Collections stringify as their type name otherwise; join a bounded preview instead.
            System.Collections.IEnumerable e => string.Join(", ",
                e.Cast<object?>().Take(20).Select(i => i?.ToString() ?? "")),
            _ => value.ToString(),
        };

        return text is null ? null : TextUtil.Truncate(text, MaxValueLength);
    }
}
