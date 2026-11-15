using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace AiOps.Mcp.Remoting;

/// <summary>
/// English performance counter name → PDH counter index. Counter resolution is done by
/// index, never by name, because counter names are localized (Turkish servers in the fleet).
/// Indices verified against the Perflib 009 registry table on Windows Server 2025.
/// </summary>
public static partial class CounterCatalog
{
    // Performance objects
    private static readonly Dictionary<string, int> Objects = new(StringComparer.OrdinalIgnoreCase)
    {
        ["System"] = 2,
        ["Memory"] = 4,
        ["Process"] = 230,
        ["PhysicalDisk"] = 234,
        ["LogicalDisk"] = 236,
        ["Processor"] = 238,
        ["Server"] = 330,
        ["Network Interface"] = 510,
        ["Paging File"] = 700,
        // NOTE: 'Processor Information' (index 2936) intentionally omitted. All base-provider
        // objects sit below the extensible-provider boundary (1847); 2936 is registration-order
        // dependent and varies across OS builds, so a fixed value would produce a wrong or
        // missing localized path on other fleet members. Use 'Processor' (238) instead.
    };

    // Counters
    private static readonly Dictionary<string, int> Counters = new(StringComparer.OrdinalIgnoreCase)
    {
        ["% Processor Time"] = 6,
        ["Available Bytes"] = 24,
        ["Committed Bytes"] = 26,
        ["Page Faults/sec"] = 28,
        ["Pages/sec"] = 40,
        ["Processor Queue Length"] = 44,
        ["Pool Paged Bytes"] = 56,
        ["Pool Nonpaged Bytes"] = 58,
        ["% User Time"] = 142,
        ["% Privileged Time"] = 144,
        ["Context Switches/sec"] = 146,
        ["Interrupts/sec"] = 148,
        ["Working Set"] = 180,
        ["Private Bytes"] = 186,
        ["Current Disk Queue Length"] = 198,
        ["% Disk Time"] = 200,
        ["Avg. Disk sec/Read"] = 208,
        ["Avg. Disk sec/Write"] = 210,
        ["Disk Reads/sec"] = 214,
        ["Disk Writes/sec"] = 216,
        ["Disk Bytes/sec"] = 218,
        ["Bytes Received/sec"] = 264,
        ["Bytes Total/sec"] = 388,
        ["% Free Space"] = 408,
        ["Free Megabytes"] = 410,
        ["Bytes Sent/sec"] = 506,
        ["Current Bandwidth"] = 520,
        ["Output Queue Length"] = 544,
        ["System Up Time"] = 674,
        ["Thread Count"] = 680,
        ["Elapsed Time"] = 684,
        ["% Usage"] = 702,
        ["Cache Bytes"] = 818,
        ["Handle Count"] = 952,
        ["Available MBytes"] = 1382,
        ["Avg. Disk Queue Length"] = 1400,
        ["% Committed Bytes In Use"] = 1406,
        ["IO Read Bytes/sec"] = 1420,
        ["IO Write Bytes/sec"] = 1422,
        ["% Idle Time"] = 1482,
    };

    [GeneratedRegex(@"^\\(?<object>[^\\(]+?)(?:\((?<instance>.+)\))?\\(?<counter>.+)$")]
    private static partial Regex PathRegex();

    /// <summary>Splits "\Object(instance)\Counter" (a leading \\machine prefix is not accepted).</summary>
    public static bool TryParsePath(string path, [NotNullWhen(true)] out string? objectName, out string? instance, [NotNullWhen(true)] out string? counterName)
    {
        objectName = null;
        instance = null;
        counterName = null;

        if (string.IsNullOrWhiteSpace(path) || path.StartsWith(@"\\", StringComparison.Ordinal))
            return false;

        var match = PathRegex().Match(path);
        if (!match.Success)
            return false;

        objectName = match.Groups["object"].Value;
        instance = match.Groups["instance"] is { Success: true } g ? g.Value : null;
        counterName = match.Groups["counter"].Value;
        return true;
    }

    /// <summary>
    /// Translates an English counter path to the target's locale using its Perflib
    /// index→name table. Returns null when the path is not in the catalog or the target
    /// table lacks the index; callers then fall back to the path as given.
    /// </summary>
    public static string? Translate(string englishPath, IReadOnlyDictionary<int, string> targetIndexToName)
    {
        if (!TryParsePath(englishPath, out var objectName, out var instance, out var counterName))
            return null;

        if (!Objects.TryGetValue(objectName, out var objectIndex) ||
            !Counters.TryGetValue(counterName, out var counterIndex))
            return null;

        if (!targetIndexToName.TryGetValue(objectIndex, out var localObject) ||
            !targetIndexToName.TryGetValue(counterIndex, out var localCounter))
            return null;

        return instance is null
            ? $@"\{localObject}\{localCounter}"
            : $@"\{localObject}({instance})\{localCounter}";
    }

    /// <summary>
    /// Parses a Perflib "Counter" REG_MULTI_SZ (alternating index/name pairs) into an
    /// index→name map, e.g. from HKLM:\...\Perflib\CurrentLanguage.
    /// </summary>
    public static IReadOnlyDictionary<int, string> ParsePerflibTable(IReadOnlyList<string> multiSz)
    {
        var map = new Dictionary<int, string>();
        for (var i = 0; i + 1 < multiSz.Count; i += 2)
        {
            if (int.TryParse(multiSz[i], out var index) && !map.ContainsKey(index))
                map[index] = multiSz[i + 1];
        }

        return map;
    }
}
