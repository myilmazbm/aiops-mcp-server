using System.Management.Automation;
using AiOps.Mcp.Remoting;
using AiOps.Mcp.Tools.GetCounters;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using NSubstitute;

namespace AiOps.Mcp.Tests;

public class GetCountersToolTests
{
    private const string EnglishCpuPath = @"\Processor(_Total)\% Processor Time";

    private readonly IRemoteExecutor _executor = Substitute.For<IRemoteExecutor>();
    private readonly CounterNameTableCache _cache = new();
    private readonly List<RemoteCommand> _sentCommands = [];

    private sealed class FakeSample
    {
        public string Path { get; init; } = "";
        public double CookedValue { get; init; }
        public DateTime Timestamp { get; init; }
    }

    private sealed class FakeRegistryValue
    {
        public string[] Counter { get; init; } = [];
    }

    /// <summary>Flat PerformanceCounterSample objects, as produced by the remote -ExpandProperty.</summary>
    private static PSObject[] Samples(DateTime timestamp, params (string Path, double Value)[] samples)
        => samples.Select(s => PSObject.AsPSObject(new FakeSample
        {
            Path = s.Path,
            CookedValue = s.Value,
            Timestamp = timestamp,
        })).ToArray();

    /// <summary>Routes mocked InvokeAsync by command name: registry table fetch vs Get-Counter.</summary>
    private void SetupServer(
        string server,
        ServerCallResult? tableResult = null,
        ServerCallResult? counterResult = null,
        string[]? perflibTable = null)
    {
        perflibTable ??= ["238", "Processor", "6", "% Processor Time"]; // English identity table
        tableResult ??= new ServerCallResult
        {
            Server = server,
            Status = ServerCallStatus.Ok,
            Output = [PSObject.AsPSObject(new FakeRegistryValue { Counter = perflibTable })],
        };
        counterResult ??= new ServerCallResult
        {
            Server = server,
            Status = ServerCallStatus.Ok,
            Output =
            [
                .. Samples(new DateTime(2026, 7, 6, 10, 0, 0), ($@"\\{server}\processor(_total)\% processor time", 10.0)),
                .. Samples(new DateTime(2026, 7, 6, 10, 0, 1), ($@"\\{server}\processor(_total)\% processor time", 30.0)),
                .. Samples(new DateTime(2026, 7, 6, 10, 0, 2), ($@"\\{server}\processor(_total)\% processor time", 20.0)),
            ],
        };

        _executor.InvokeAsync(server, Arg.Any<RemoteCommand>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var command = call.Arg<RemoteCommand>();
                lock (_sentCommands)
                {
                    _sentCommands.Add(command);
                }

                return command.CommandName == FleetCommands.GetItemProperty ? tableResult : counterResult;
            });
    }

    private Task<AiOps.Contracts.GetCountersResult> Call(
        string[]? servers = null, string[]? paths = null, int samples = 3, int interval = 1,
        bool includeSamples = false, int maxSeries = GetCountersTool.DefaultMaxSeries)
        => GetCountersTool.GetCounters(
            _executor, _cache, Options.Create(new RemotingOptions()), NullLoggerFactory.Instance,
            servers ?? ["srv1"], paths ?? [EnglishCpuPath], samples, interval,
            include_samples: includeSamples, max_series: maxSeries);

    [Fact]
    public async Task Counter_samples_are_expanded_remotely_to_survive_serialization_depth()
    {
        SetupServer("srv1");

        await Call();

        // Without the remote -ExpandProperty, PSRP depth-1 serialization reduces nested
        // CounterSamples to bare type-name strings (seen against a real JEA endpoint).
        var counterCommand = _sentCommands.Single(c => c.CommandName == FleetCommands.GetCounter);
        // Module-qualified: the restricted proxy function would otherwise shadow the cmdlet.
        Assert.Equal(@"Microsoft.PowerShell.Utility\Select-Object", counterCommand.Pipe!.CommandName);
        Assert.Equal("CounterSamples", counterCommand.Pipe.Parameters["ExpandProperty"]);
    }

    [Fact]
    public async Task Computes_min_max_avg_last_from_samples()
    {
        SetupServer("srv1");

        var result = await Call();

        var server = Assert.Single(result.Results);
        Assert.Equal("ok", server.Status);
        var stats = Assert.Single(server.Counters);
        Assert.Equal(EnglishCpuPath, stats.Path);
        Assert.Equal(10.0, stats.Min);
        Assert.Equal(30.0, stats.Max);
        Assert.Equal(20.0, stats.Avg, precision: 10);
        Assert.Equal(20.0, stats.Last);
        Assert.Equal(3, stats.SampleCount);
        Assert.Null(stats.Samples); // raw samples only on request
    }

    [Fact]
    public async Task Raw_samples_returned_only_when_requested()
    {
        SetupServer("srv1");

        var result = await Call(includeSamples: true);

        var samples = result.Results[0].Counters[0].Samples;
        Assert.NotNull(samples);
        Assert.Equal(3, samples.Count);
        Assert.Equal(30.0, samples[1].Value);
    }

    [Fact]
    public async Task Translates_paths_using_the_target_locale_table()
    {
        SetupServer(
            "srv-tr",
            perflibTable: ["238", "İşlemci", "6", "% İşlemci Zamanı"],
            counterResult: new ServerCallResult
            {
                Server = "srv-tr",
                Status = ServerCallStatus.Ok,
                // PDH lowercases returned paths with the OS culture: İ → i on tr-TR.
                Output = [.. Samples(new DateTime(2026, 7, 6, 10, 0, 0), (@"\\srv-tr\işlemci(_total)\% işlemci zamanı", 42.0))],
            });

        var result = await Call(servers: ["srv-tr"]);

        var counterCommand = _sentCommands.Single(c => c.CommandName == FleetCommands.GetCounter);
        var requestedPaths = Assert.IsType<string[]>(counterCommand.Parameters["Counter"]);
        Assert.Equal(@"\İşlemci(_Total)\% İşlemci Zamanı", requestedPaths.Single());

        // The Turkish-lowercased returned path must still map back to the requested English path.
        var stats = Assert.Single(result.Results[0].Counters);
        Assert.Equal(EnglishCpuPath, stats.Path);
        Assert.Equal(42.0, stats.Last);
        Assert.Equal("ok", result.Results[0].Status);
    }

    [Fact]
    public async Task Wildcard_instances_produce_one_entry_per_returned_path()
    {
        SetupServer(
            "srv1",
            perflibTable: ["236", "LogicalDisk", "408", "% Free Space"],
            counterResult: new ServerCallResult
            {
                Server = "srv1",
                Status = ServerCallStatus.Ok,
                Output =
                [
                    .. Samples(new DateTime(2026, 7, 6, 10, 0, 0),
                        (@"\\srv1\logicaldisk(c:)\% free space", 55.0),
                        (@"\\srv1\logicaldisk(d:)\% free space", 80.0)),
                ],
            });

        var result = await Call(paths: [@"\LogicalDisk(*)\% Free Space"]);

        var counters = result.Results[0].Counters;
        Assert.Equal(2, counters.Count);
        Assert.All(counters, c => Assert.Equal(@"\LogicalDisk(*)\% Free Space", c.Path));
        Assert.Equal(2, counters.Select(c => c.LocalizedPath).Distinct().Count());
    }

    [Fact]
    public async Task Unreachable_server_short_circuits_without_querying_counters()
    {
        SetupServer("srv1", tableResult: new ServerCallResult
        {
            Server = "srv1",
            Status = ServerCallStatus.Unreachable,
            Error = "timeout",
        });

        var result = await Call();

        Assert.Equal("unreachable", result.Results[0].Status);
        Assert.DoesNotContain(_sentCommands, c => c.CommandName == FleetCommands.GetCounter);
    }

    [Fact]
    public async Task Table_fetch_failure_falls_back_to_english_paths()
    {
        SetupServer("srv1", tableResult: new ServerCallResult
        {
            Server = "srv1",
            Status = ServerCallStatus.Error,
            Error = "registry read denied",
        });

        var result = await Call();

        var counterCommand = _sentCommands.Single(c => c.CommandName == FleetCommands.GetCounter);
        var requestedPaths = Assert.IsType<string[]>(counterCommand.Parameters["Counter"]);
        Assert.Equal(EnglishCpuPath, requestedPaths.Single());
        Assert.Equal("ok", result.Results[0].Status);
    }

    [Fact]
    public async Task Locale_table_is_cached_per_server()
    {
        SetupServer("srv1");

        await Call();
        await Call();

        Assert.Equal(1, _sentCommands.Count(c => c.CommandName == FleetCommands.GetItemProperty));
        Assert.Equal(2, _sentCommands.Count(c => c.CommandName == FleetCommands.GetCounter));
    }

    [Fact]
    public async Task Counter_errors_alongside_data_yield_partial_status()
    {
        SetupServer("srv1", counterResult: new ServerCallResult
        {
            Server = "srv1",
            Status = ServerCallStatus.Error,
            Error = "counter path not found",
            Output = [.. Samples(new DateTime(2026, 7, 6, 10, 0, 0), (@"\\srv1\processor(_total)\% processor time", 5.0))],
        });

        var result = await Call();

        Assert.Equal("partial", result.Results[0].Status);
        Assert.Equal("counter path not found", result.Results[0].Error);
        Assert.Single(result.Results[0].Counters);
    }

    [Fact]
    public async Task Counter_call_with_no_data_is_an_error()
    {
        SetupServer("srv1", counterResult: new ServerCallResult
        {
            Server = "srv1",
            Status = ServerCallStatus.Error,
            Error = "boom",
        });

        var result = await Call();

        Assert.Equal("error", result.Results[0].Status);
        Assert.Equal("boom", result.Results[0].Error);
    }

    [Fact]
    public async Task Sample_count_is_clamped_to_the_collection_window_not_rejected()
    {
        SetupServer("srv1");

        // 10 samples × 5 s = 50 s > 25 s window: the request is clamped, not thrown.
        await Call(samples: 10, interval: 5);

        var counterCommand = _sentCommands.Single(c => c.CommandName == FleetCommands.GetCounter);
        var maxSamples = Assert.IsType<int>(counterCommand.Parameters["MaxSamples"]);
        Assert.Equal(GetCountersTool.MaxCollectionSeconds / 5, maxSamples); // 5
    }

    [Fact]
    public async Task The_advertised_max_samples_is_usable_at_interval_one()
    {
        SetupServer("srv1");

        // The documented ceiling must not throw (it used to: MaxSamples×1 > window).
        var result = await Call(samples: GetCountersTool.MaxSamples, interval: 1);

        Assert.Equal("ok", result.Results[0].Status);
        var counterCommand = _sentCommands.Single(c => c.CommandName == FleetCommands.GetCounter);
        Assert.Equal(GetCountersTool.MaxSamples, counterCommand.Parameters["MaxSamples"]);
    }

    [Fact]
    public async Task Dotless_turkish_i_in_returned_path_still_maps_back_to_the_english_request()
    {
        // English fallback (empty locale table): the requested path keeps ASCII 'I'
        // ("Network Interface"), but PDH on a tr-TR box lowercases the returned path with a
        // dotless 'ı'. The İ/ı fold in NormalizePath must still match them.
        const string requested = @"\Network Interface(Ethernet)\Bytes Total/sec";
        SetupServer(
            "srv-tr",
            perflibTable: ["9999", "Unused"], // no translation → English fallback
            counterResult: new ServerCallResult
            {
                Server = "srv-tr",
                Status = ServerCallStatus.Ok,
                Output = [.. Samples(new DateTime(2026, 7, 6, 10, 0, 0),
                    (@"\\srv-tr\network ınterface(ethernet)\bytes total/sec", 123.0))],
            });

        var result = await Call(servers: ["srv-tr"], paths: [requested]);

        var stats = Assert.Single(result.Results[0].Counters);
        Assert.Equal(requested, stats.Path);
        Assert.Equal(123.0, stats.Last);
    }

    [Fact]
    public async Task Wildcard_expansion_does_not_mask_a_second_path_returning_no_data()
    {
        // Two paths requested; only the wildcard one expands (to 2 series), the CPU path
        // returns nothing. Count (2) >= requested (2) but coverage is incomplete → partial.
        SetupServer(
            "srv1",
            perflibTable: ["236", "LogicalDisk", "408", "% Free Space", "238", "Processor", "6", "% Processor Time"],
            counterResult: new ServerCallResult
            {
                Server = "srv1",
                Status = ServerCallStatus.Ok,
                Output =
                [
                    .. Samples(new DateTime(2026, 7, 6, 10, 0, 0),
                        (@"\\srv1\logicaldisk(c:)\% free space", 55.0),
                        (@"\\srv1\logicaldisk(d:)\% free space", 80.0)),
                ],
            });

        var result = await Call(paths: [@"\LogicalDisk(*)\% Free Space", EnglishCpuPath]);

        Assert.Equal("partial", result.Results[0].Status);
        Assert.Contains(EnglishCpuPath, result.Results[0].Error);
    }

    [Fact]
    public async Task Missing_sample_timestamp_does_not_crash_the_call()
    {
        // default(DateTime) + a positive local offset underflows DateTimeOffset; the same
        // guard get_events has must exist here, or one bad sample kills every server's result.
        SetupServer("srv1", counterResult: new ServerCallResult
        {
            Server = "srv1",
            Status = ServerCallStatus.Ok,
            Output = [PSObject.AsPSObject(new { Path = @"\\srv1\processor(_total)\% processor time", CookedValue = 7.0 })],
        });

        var result = await Call();

        Assert.Equal("ok", result.Results[0].Status);
        Assert.Equal(7.0, Assert.Single(result.Results[0].Counters).Last);
    }

    [Fact]
    public async Task One_servers_unexpected_exception_does_not_lose_the_others()
    {
        SetupServer("srv1");
        _executor.InvokeAsync("srv2", Arg.Any<RemoteCommand>(), Arg.Any<CancellationToken>())
            .Returns<Task<ServerCallResult>>(_ => throw new InvalidOperationException("mapping blew up"));

        var result = await Call(servers: ["srv1", "srv2"]);

        Assert.Equal("ok", result.Results[0].Status);
        Assert.Equal("error", result.Results[1].Status);
        Assert.Contains("mapping blew up", result.Results[1].Error);
    }

    [Fact]
    public async Task Overlapping_wildcard_and_specific_paths_are_both_covered_and_attributed_specifically()
    {
        // The target emits the _total series twice (once per request). It must be deduped,
        // attributed to the SPECIFIC request, and BOTH requests count as covered → ok.
        const string specific = @"\Processor(_Total)\% Processor Time";
        const string wildcard = @"\Processor(*)\% Processor Time";
        SetupServer("srv1", counterResult: new ServerCallResult
        {
            Server = "srv1",
            Status = ServerCallStatus.Ok,
            Output =
            [
                .. Samples(new DateTime(2026, 7, 6, 10, 0, 0),
                    (@"\\srv1\processor(0)\% processor time", 1.0),
                    (@"\\srv1\processor(_total)\% processor time", 5.0),
                    (@"\\srv1\processor(_total)\% processor time", 5.0)), // duplicate emission
            ],
        });

        var result = await Call(paths: [wildcard, specific]);

        Assert.Equal("ok", result.Results[0].Status);
        Assert.Null(result.Results[0].Error);
        var total = result.Results[0].Counters.Single(c => c.LocalizedPath?.Contains("_total") == true || c.Path == specific);
        Assert.Equal(specific, total.Path); // most specific request wins attribution
        Assert.Equal(1, total.SampleCount);  // duplicate tick deduped
    }

    [Fact]
    public async Task Collection_window_follows_the_configured_per_server_timeout()
    {
        SetupServer("srv1");

        // 12 s budget − 5 s headroom = 7 s window at interval 1 → samples clamp to 7, not 25.
        await GetCountersTool.GetCounters(
            _executor, _cache, Options.Create(new RemotingOptions { PerServerTimeoutSeconds = 12 }),
            NullLoggerFactory.Instance, ["srv1"], [EnglishCpuPath], samples: 25, interval: 1);

        var counterCommand = _sentCommands.Single(c => c.CommandName == FleetCommands.GetCounter);
        Assert.Equal(7, counterCommand.Parameters["MaxSamples"]);
    }

    [Fact]
    public async Task Locale_table_failure_is_surfaced_when_the_counter_call_fails()
    {
        SetupServer(
            "srv-tr",
            tableResult: new ServerCallResult { Server = "srv-tr", Status = ServerCallStatus.Error, Error = "registry denied" },
            counterResult: new ServerCallResult { Server = "srv-tr", Status = ServerCallStatus.Error, Error = "counter path could not be interpreted" });

        var result = await Call(servers: ["srv-tr"]);

        // The LLM must see the root cause (no locale table), not only Get-Counter's symptom.
        Assert.Equal("error", result.Results[0].Status);
        Assert.Contains("counter path could not be interpreted", result.Results[0].Error);
        Assert.Contains("registry denied", result.Results[0].Error);
    }

    [Fact]
    public async Task Rejects_machine_prefixed_paths()
        => await Assert.ThrowsAsync<McpException>(() => Call(paths: [@"\\srv9\Processor(_Total)\% Processor Time"]));

    [Fact]
    public async Task Rejects_more_than_the_path_limit()
        => await Assert.ThrowsAsync<McpException>(() => Call(
            paths: Enumerable.Range(0, GetCountersTool.MaxCounterPaths + 1).Select(i => $@"\Memory\Counter{i}").ToArray()));
    // --- series cap (invariant 4) ---

    /// <summary>A wildcard path expands to one series per instance; this fakes that expansion.</summary>
    private ServerCallResult WideResult(string server, int seriesCount)
        => new()
        {
            Server = server,
            Status = ServerCallStatus.Ok,
            Output = [.. Samples(
                new DateTime(2026, 7, 6, 10, 0, 0),
                [.. Enumerable.Range(0, seriesCount).Select(i => ($@"\{server}\process(p{i:D4})\% processor time", (double)i))])],
        };

    [Fact]
    public async Task Series_beyond_the_cap_are_cut_and_flagged()
    {
        // Measured on a quiet lab web server: one wildcard path returned 101 series, two
        // returned 1102 and 177 KB of JSON. That is what the cap exists to stop.
        SetupServer("srv1", counterResult: WideResult("srv1", 300));

        var result = await Call(paths: [@"\Process(*)\% Processor Time"], maxSeries: 50);

        var server = Assert.Single(result.Results);
        Assert.Equal(50, server.Counters.Count);
        Assert.True(server.Truncated);
    }

    [Fact]
    public async Task A_result_inside_the_cap_is_not_flagged_as_truncated()
    {
        SetupServer("srv1", counterResult: WideResult("srv1", 3));

        var result = await Call(paths: [@"\Process(*)\% Processor Time"], maxSeries: 50);

        var server = Assert.Single(result.Results);
        Assert.Equal(3, server.Counters.Count);
        Assert.False(server.Truncated);
    }

    [Fact]
    public async Task Which_series_survive_the_cap_is_stable_across_identical_calls()
    {
        // PDH enumeration order is not stable, so without an explicit ordering the same
        // question would return a different slice of counters each time it was asked.
        var wide = WideResult("srv1", 120);
        SetupServer("srv1", counterResult: wide);
        var first = await Call(paths: [@"\Process(*)\% Processor Time"], maxSeries: 10);

        var reversed = new ServerCallResult
        {
            Server = "srv1",
            Status = ServerCallStatus.Ok,
            Output = [.. wide.Output.Reverse()],
        };
        SetupServer("srv1", counterResult: reversed);
        var second = await Call(paths: [@"\Process(*)\% Processor Time"], maxSeries: 10);

        Assert.Equal(
            first.Results[0].Counters.Select(c => c.LocalizedPath ?? c.Path),
            second.Results[0].Counters.Select(c => c.LocalizedPath ?? c.Path));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(int.MaxValue)]
    public async Task Absurd_series_caps_are_clamped_never_honored(int requested)
    {
        SetupServer("srv1", counterResult: WideResult("srv1", 300));

        var result = await Call(paths: [@"\Process(*)\% Processor Time"], maxSeries: requested);

        Assert.InRange(result.Results[0].Counters.Count, 1, GetCountersTool.MaxSeriesCeiling);
    }

    [Fact]
    public async Task Requesting_raw_samples_lowers_the_series_ceiling()
    {
        // Each series then carries up to 25 sample points rather than four summary numbers,
        // so the full ceiling would put hundreds of kilobytes into one response.
        SetupServer("srv1", counterResult: WideResult("srv1", 300));

        var result = await Call(
            paths: [@"\Process(*)\% Processor Time"], includeSamples: true, maxSeries: GetCountersTool.MaxSeriesCeiling);

        Assert.Equal(GetCountersTool.MaxSeriesCeilingWithSamples, result.Results[0].Counters.Count);
        Assert.True(result.Results[0].Truncated);
    }
}
