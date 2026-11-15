using System.ComponentModel;
using System.Diagnostics;
using System.Management.Automation;
using System.Text.RegularExpressions;
using AiOps.Contracts;
using AiOps.Mcp.Remoting;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace AiOps.Mcp.Tools.GetCounters;

[McpServerToolType]
public static class GetCountersTool
{
    public const int DefaultSamples = 5;
    /// <summary>Absolute sample ceiling at interval 1; larger intervals lower it (see MaxCollectionSeconds).</summary>
    public const int MaxSamples = 25;
    public const int MaxIntervalSeconds = 10;
    /// <summary>Upper bound on samples×interval; further capped by the configured per-server timeout minus headroom.</summary>
    public const int MaxCollectionSeconds = 25;
    /// <summary>Seconds reserved for pipeline setup/teardown inside the per-server budget.</summary>
    public const int TimeoutHeadroomSeconds = 5;
    public const int MaxCounterPaths = 20;
    /// <summary>
    /// Default number of counter series returned per server. A single wildcard path such as
    /// '\Process(*)\% Processor Time' expands to one series per process — measured at 101 on a
    /// quiet lab web server and over a thousand for two wildcards — so without this the tool
    /// could push hundreds of kilobytes into the LLM context from one call.
    /// </summary>
    public const int DefaultMaxSeries = 50;
    /// <summary>Hard ceiling; requests above it are clamped, never honored.</summary>
    public const int MaxSeriesCeiling = 200;
    /// <summary>
    /// Lower series ceiling when raw samples are requested: each series then carries up to
    /// MaxSamples points instead of four summary numbers, so the full ceiling would put a few
    /// hundred kilobytes in one response. Asking for raw samples means drilling into specific
    /// counters, not sweeping a wildcard.
    /// </summary>
    public const int MaxSeriesCeilingWithSamples = 25;

    [McpServerTool(Name = "get_counters", ReadOnly = true)]
    [Description("Samples performance counters on one or more servers and returns summary statistics (min/max/avg/last). Counter paths are given with English names and resolved to the target's locale via PDH indices. Raw samples are returned only when include_samples is true.")]
    public static async Task<GetCountersResult> GetCounters(
        IRemoteExecutor executor,
        CounterNameTableCache tableCache,
        IOptions<RemotingOptions> remotingOptions,
        ILoggerFactory loggerFactory,
        [Description("Target server names (FQDN)")] string[] servers,
        [Description(@"English counter paths, e.g. '\Processor(_Total)\% Processor Time'")] string[] counter_paths,
        [Description("Number of samples per counter (default 5, max 25; effective max is lower at larger intervals)")] int samples = DefaultSamples,
        [Description("Seconds between samples (default 1, max 10)")] int interval = 1,
        [Description("Return raw samples in addition to summary statistics")] bool include_samples = false,
        [Description("Maximum counter series per server (default 50, hard cap 200). A wildcard path expands to one series per instance.")] int max_series = DefaultMaxSeries,
        CancellationToken cancellationToken = default)
    {
        if (servers is not { Length: > 0 })
            throw new McpException("servers must contain at least one server name.");
        if (counter_paths is not { Length: > 0 })
            throw new McpException("counter_paths must contain at least one counter path.");
        if (counter_paths.Length > MaxCounterPaths)
            throw new McpException($"counter_paths is limited to {MaxCounterPaths} paths per call.");

        interval = Math.Clamp(interval, 1, MaxIntervalSeconds);
        // The collection window must fit inside the configured per-server timeout (minus
        // headroom), not a hard-coded assumption of 30 s — lowering Remoting:PerServerTimeoutSeconds
        // must not silently turn every long sample into a timeout. Clamp, never throw, so the
        // advertised maximum is always a valid call.
        var window = Math.Max(1, Math.Min(MaxCollectionSeconds, remotingOptions.Value.PerServerTimeoutSeconds - TimeoutHeadroomSeconds));
        var maxSamplesForInterval = Math.Max(1, window / interval);
        samples = Math.Clamp(samples, 1, Math.Min(MaxSamples, maxSamplesForInterval));
        var seriesCeiling = include_samples ? MaxSeriesCeilingWithSamples : MaxSeriesCeiling;
        var maxSeries = Math.Clamp(max_series, 1, seriesCeiling);

        foreach (var path in counter_paths)
        {
            if (!CounterCatalog.TryParsePath(path, out _, out _, out _))
                throw new McpException($@"Invalid counter path '{path}'. Expected '\Object(instance)\Counter' without a machine prefix.");
        }

        var logger = loggerFactory.CreateLogger("AiOps.Mcp.Tools.get_counters");
        var stopwatch = Stopwatch.StartNew();
        var results = await FanOut.RunAsync(
            servers, remotingOptions.Value.MaxConcurrency,
            server => QueryServerSafe(executor, tableCache, logger, server, counter_paths, samples, interval, include_samples, maxSeries, cancellationToken),
            cancellationToken).ConfigureAwait(false);

        logger.LogInformation(
            "get_counters servers={ServerCount} paths={PathCount} samples={Samples}x{Interval}s took {ElapsedMs} ms",
            servers.Length, counter_paths.Length, samples, interval, stopwatch.ElapsedMilliseconds);

        return new GetCountersResult { Results = results };
    }

    /// <summary>One server's failure (mapping bug, unexpected shape) must never take the other servers' results down with it.</summary>
    private static async Task<ServerCountersResult> QueryServerSafe(
        IRemoteExecutor executor, CounterNameTableCache tableCache, ILogger logger, string server,
        string[] counterPaths, int samples, int interval, bool includeSamples, int maxSeries, CancellationToken cancellationToken)
    {
        try
        {
            return await QueryServer(executor, tableCache, logger, server, counterPaths, samples, interval, includeSamples, maxSeries, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "get_counters failed for {Server}", server);
            return new ServerCountersResult { Server = server, Status = "error", Error = ex.Message };
        }
    }

    private static async Task<ServerCountersResult> QueryServer(
        IRemoteExecutor executor,
        CounterNameTableCache tableCache,
        ILogger logger,
        string server,
        string[] counterPaths,
        int samples,
        int interval,
        bool includeSamples,
        int maxSeries,
        CancellationToken cancellationToken)
    {
        var fetch = await tableCache.GetAsync(server, executor, cancellationToken).ConfigureAwait(false);
        if (fetch.Table is null && fetch.FailedCall?.Status == ServerCallStatus.Unreachable)
            return new ServerCountersResult { Server = server, Status = "unreachable", Error = fetch.FailedCall.Error };

        // Without a locale table, fall back to the English paths (correct on English servers).
        // The fetch failure is kept: if the counter call then fails, the LLM must see the real
        // root cause (locale table unreadable) rather than only Get-Counter's symptom.
        var tableProblem = fetch.Table is null ? fetch.FailedCall?.Error ?? "locale table unavailable" : null;
        if (tableProblem is not null)
            logger.LogWarning("Perflib locale table unavailable on {Server}; using English counter names: {Error}", server, tableProblem);

        var table = fetch.Table ?? new Dictionary<int, string>();
        var localized = counterPaths
            .Select(p => (Requested: p, Actual: CounterCatalog.Translate(p, table) ?? p))
            .ToList();

        // CounterSamples is expanded remotely: PSRP serializes nested objects only to
        // depth 1, so without the Select-Object the samples arrive as bare type names.
        // Module-qualified on purpose — the restricted RestrictedRemoteServer proxy
        // function shadows the real cmdlet the role capability exposes.
        var call = await executor.InvokeAsync(
            server,
            RemoteCommand.Create(
                    FleetCommands.GetCounter,
                    ("Counter", localized.Select(l => l.Actual).ToArray()),
                    ("MaxSamples", samples),
                    ("SampleInterval", interval))
                .PipeTo(FleetCommands.SelectObject, ("ExpandProperty", "CounterSamples")),
            cancellationToken).ConfigureAwait(false);

        if (call.Status == ServerCallStatus.Unreachable)
            return new ServerCountersResult { Server = server, Status = "unreachable", Error = call.Error };

        if (call.Output.Count == 0)
            return new ServerCountersResult
            {
                Server = server,
                Status = "error",
                Error = WithTableProblem(call.Error ?? "No counter data returned", tableProblem),
            };

        var matcher = new PathMatcher(localized);
        var counters = BuildStats(call.Output, matcher, includeSamples);

        // Coverage, not count: a wildcard path can expand to many series and inflate the
        // count while another requested path returned nothing (PS 5.1 emits no error for a
        // non-matching wildcard). A requested path is covered when ANY returned series
        // matches it — tested independently per request, so a wildcard and a specific path
        // that overlap are both satisfied by the same series.
        var missing = localized
            .Where(l => !counters.Any(c => matcher.Matches(l.Requested, c.LocalizedPath ?? c.Path)))
            .Select(l => l.Requested)
            .ToList();
        var status = call.Status == ServerCallStatus.Ok && missing.Count == 0 ? "ok" : "partial";

        var error = call.Status != ServerCallStatus.Ok
            ? WithTableProblem(call.Error, tableProblem)
            : missing.Count > 0 ? $"No data for: {string.Join(", ", missing)}" : null;

        // Mandatory output cap. Get-Counter emits a flat sample stream (one object per path per
        // tick), so a server-side Select-Object -First would cut a series in half rather than
        // drop whole series — the cap has to be applied here, after the stream is grouped, the
        // same client-side backstop get_events keeps. Ordering is fixed so that which series
        // survive the cut is stable between identical calls instead of PDH enumeration order.
        var truncated = counters.Count > maxSeries;
        if (truncated)
        {
            logger.LogInformation(
                "get_counters truncated {Server} from {Total} to {Cap} series", server, counters.Count, maxSeries);
            counters = counters
                .OrderBy(c => c.Path, StringComparer.Ordinal)
                .ThenBy(c => c.LocalizedPath ?? string.Empty, StringComparer.Ordinal)
                .Take(maxSeries)
                .ToList();
        }

        return new ServerCountersResult
        {
            Server = server,
            Status = status,
            Error = error,
            Truncated = truncated,
            Counters = counters,
        };
    }

    private static string? WithTableProblem(string? error, string? tableProblem)
        => tableProblem is null ? error : $"{error} (locale table unavailable: {tableProblem})";

    private static List<CounterStatsDto> BuildStats(IReadOnlyList<PSObject> samples, PathMatcher matcher, bool includeSamples)
    {
        // Flat PerformanceCounterSample stream: one object per path per sampling tick.
        // Deduped by (path, timestamp): when two requests overlap (a wildcard and a specific
        // path), the target emits the same series twice per tick.
        var series = new Dictionary<string, Dictionary<DateTimeOffset, CounterSampleDto>>(StringComparer.Ordinal);
        foreach (var sample in samples)
        {
            var path = PsValue.Get<string>(sample, "Path");
            if (path is null)
                continue;

            // default(DateTime) + a positive local offset underflows DateTimeOffset (same
            // guard as get_events); a missing timestamp must not kill the whole call.
            var raw = PsValue.Get<DateTime>(sample, "Timestamp");
            DateTimeOffset timestamp = raw == default ? DateTimeOffset.MinValue : raw;

            if (!series.TryGetValue(path, out var points))
                series[path] = points = [];
            points.TryAdd(timestamp, new CounterSampleDto
            {
                Timestamp = timestamp,
                Value = PsValue.Get<double>(sample, "CookedValue"),
            });
        }

        var stats = new List<CounterStatsDto>();
        foreach (var (returnedPath, byTime) in series)
        {
            if (byTime.Count == 0)
                continue;

            var points = byTime.Values.OrderBy(p => p.Timestamp).ToList();
            var requested = matcher.Attribute(returnedPath);
            var values = points.Select(p => p.Value).ToList();
            stats.Add(new CounterStatsDto
            {
                Path = requested ?? returnedPath,
                LocalizedPath = NormalizePath(returnedPath) == NormalizePath(requested ?? returnedPath) ? null : returnedPath,
                Min = values.Min(),
                Max = values.Max(),
                Avg = values.Average(),
                Last = points[^1].Value,
                SampleCount = points.Count,
                Samples = includeSamples ? points : null,
            });
        }

        return stats;
    }

    /// <summary>
    /// Maps returned sample paths (\\machine\object(inst)\counter) back to requested English
    /// paths via the localized patterns they were queried with. Regexes are compiled once per
    /// call, not per (series × request) comparison.
    /// </summary>
    private sealed class PathMatcher
    {
        private readonly List<(string Requested, Regex Pattern, bool IsWildcard)> _patterns;

        public PathMatcher(List<(string Requested, string Actual)> localized)
        {
            _patterns = localized.Select(l =>
            {
                var normalized = NormalizePath(l.Actual);
                var pattern = "^" + Regex.Escape(normalized).Replace(@"\*", ".*").Replace(@"\?", ".") + "$";
                var isWildcard = normalized.Contains('*') || normalized.Contains('?');
                return (l.Requested, new Regex(pattern, RegexOptions.CultureInvariant), isWildcard);
            }).ToList();
        }

        public bool Matches(string requested, string returnedPath)
        {
            var normalized = NormalizePath(returnedPath);
            return _patterns.Any(p => p.Requested == requested && p.Pattern.IsMatch(normalized));
        }

        /// <summary>Most specific match wins: an exact (non-wildcard) request over a wildcard one.</summary>
        public string? Attribute(string returnedPath)
        {
            var normalized = NormalizePath(returnedPath);
            string? wildcardHit = null;
            foreach (var (requested, pattern, isWildcard) in _patterns)
            {
                if (!pattern.IsMatch(normalized))
                    continue;
                if (!isWildcard)
                    return requested;
                wildcardHit ??= requested;
            }

            return wildcardHit;
        }
    }

    private static string NormalizePath(string path)
    {
        // Strip the \\machine prefix PDH adds to returned paths.
        if (path.StartsWith(@"\\", StringComparison.Ordinal))
        {
            var next = path.IndexOf('\\', 2);
            if (next > 0)
                path = path[next..];
        }

        // Invariant casing, then fold both Turkish dotted/dotless forms onto ASCII 'I':
        // PDH lowercases returned paths with the OS culture, so on tr-TR 'İşlemci' becomes
        // 'işlemci' (invariant-upper 'IŞLEMCI') while the requested stays 'İŞLEMCI'; and an
        // English 'I' (e.g. 'IO Read Bytes/sec', 'IPv4') lowercases to dotless 'ı'
        // (invariant-upper still 'ı'). Folding İ→I and ı→I makes every pairing compare equal.
        return path.ToUpperInvariant().Replace('İ', 'I').Replace('ı', 'I');
    }
}
