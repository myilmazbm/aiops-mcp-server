using System.Management.Automation;
using AiOps.Mcp.Inventory;
using AiOps.Mcp.Remoting;
using NSubstitute;

namespace AiOps.Mcp.Tests;

public class InventoryCollectorTests
{
    private readonly IRemoteExecutor _executor = Substitute.For<IRemoteExecutor>();
    private readonly List<RemoteCommand> _sentCommands = [];

    private InventoryCollector Collector => new(_executor);

    private void Route(Func<RemoteCommand, ServerCallResult> responder)
        => _executor.InvokeAsync(Arg.Any<string>(), Arg.Any<RemoteCommand>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var command = call.Arg<RemoteCommand>();
                lock (_sentCommands)
                {
                    _sentCommands.Add(command);
                }

                return responder(command);
            });

    private static ServerCallResult Ok(params object[] items)
        => new()
        {
            Server = "srv1",
            Status = ServerCallStatus.Ok,
            Output = items.Select(PSObject.AsPSObject).ToList(),
        };

    [Fact]
    public async Task Software_requires_display_name_and_reads_both_registry_hives()
    {
        Route(c => Ok(
            new { DisplayName = "Zulu App", DisplayVersion = "2.0", Publisher = "Zulu", InstallDate = "20260115" },
            new { PSChildName = "{guid-without-displayname}" }, // patch plumbing: skipped
            new { DisplayName = "Alpha App" }));

        var result = await Collector.CollectAsync("srv1", ["software"], CancellationToken.None);

        var command = Assert.Single(_sentCommands);
        var paths = Assert.IsType<string[]>(command.Parameters["Path"]);
        Assert.Contains(paths, p => p.Contains(@"\WOW6432Node\", StringComparison.Ordinal));
        Assert.Contains(paths, p => !p.Contains(@"\WOW6432Node\", StringComparison.Ordinal));

        var software = result.Snapshot.Software!;
        Assert.Equal(["Alpha App", "Zulu App"], software.Select(s => s.Name)); // sorted, no-name skipped
        Assert.Equal(new DateOnly(2026, 1, 15), software[1].InstallDate);
    }

    [Theory]
    [InlineData("20260115", 2026, 1, 15)]
    [InlineData(null, 0, 0, 0)]
    [InlineData("", 0, 0, 0)]
    [InlineData("2026/01/15", 0, 0, 0)] // wrong format
    [InlineData("20260230", 0, 0, 0)]   // Feb 30 does not exist
    [InlineData("garbage", 0, 0, 0)]
    public void Install_date_parses_yyyyMMdd_or_nothing(string? raw, int y, int m, int d)
    {
        var parsed = InventoryCollector.ParseInstallDate(raw);

        if (y == 0)
            Assert.Null(parsed);
        else
            Assert.Equal(new DateOnly(y, m, d), parsed);
    }

    [Fact]
    public async Task Ports_resolve_process_names_by_pid_and_never_parse_netstat()
    {
        Route(c => c.CommandName switch
        {
            FleetCommands.GetNetTcpConnection => Ok(
                new { LocalAddress = "0.0.0.0", LocalPort = 443, OwningProcess = 4 },
                new { LocalAddress = "0.0.0.0", LocalPort = 443, OwningProcess = 4 }, // dupe
                new { LocalAddress = "::", LocalPort = 135, OwningProcess = 900 }),
            FleetCommands.GetProcess => Ok(
                new { Id = 4, ProcessName = "System" },
                new { Id = 900, ProcessName = "svchost" }),
            _ => throw new InvalidOperationException(c.CommandName),
        });

        var result = await Collector.CollectAsync("srv1", ["ports"], CancellationToken.None);

        var ports = result.Snapshot.Ports!;
        Assert.Equal(2, ports.Count); // deduped
        Assert.Equal(135, ports[0].Port); // sorted by port
        Assert.Equal("svchost", ports[0].ProcessName);
        Assert.Equal("System", ports[1].ProcessName);
        Assert.Equal("Listen", _sentCommands.First(c => c.CommandName == FleetCommands.GetNetTcpConnection).Parameters["State"]);
    }

    [Fact]
    public async Task Ports_survive_a_failed_process_lookup()
    {
        Route(c => c.CommandName switch
        {
            FleetCommands.GetNetTcpConnection => Ok(new { LocalAddress = "0.0.0.0", LocalPort = 80, OwningProcess = 1234 }),
            _ => new ServerCallResult { Server = "srv1", Status = ServerCallStatus.Error, Error = "denied" },
        });

        var result = await Collector.CollectAsync("srv1", ["ports"], CancellationToken.None);

        var port = Assert.Single(result.Snapshot.Ports!);
        Assert.Equal(80, port.Port);
        Assert.Equal(1234, port.Pid);
        Assert.Null(port.ProcessName); // name resolution is best-effort
        Assert.Null(result.Outcomes.Single().Error);
    }

    [Fact]
    public async Task Gpo_deduplicates_by_id_across_extension_keys()
    {
        Route(_ => Ok(
            new { DisplayName = "Baseline Policy", GPOName = "{AAA}" },
            new { DisplayName = "Baseline Policy", GPOName = "{aaa}" }, // same GPO under another CSE key
            new { DisplayName = "Firewall Policy", GPOName = "{BBB}" },
            new { PSChildName = "0" })); // registry noise without DisplayName

        var result = await Collector.CollectAsync("srv1", ["gpo"], CancellationToken.None);

        Assert.Equal(["Baseline Policy", "Firewall Policy"], result.Snapshot.Gpo!.Select(g => g.Name));
        Assert.Equal("{AAA}", result.Snapshot.Gpo![0].Id);
    }

    [Fact]
    public async Task Gpo_reads_only_named_values_to_dodge_invalid_cast_registry_types()
    {
        Route(_ => Ok(new { DisplayName = "Baseline Policy", GPOName = "{AAA}" }));

        await Collector.CollectAsync("srv1", ["gpo"], CancellationToken.None);

        // Enumerating all values under GP History throws InvalidCastException on some
        // value types (seen on a real Server 2025 host); -Name avoids touching them.
        var names = Assert.IsType<string[]>(_sentCommands.Single().Parameters["Name"]);
        Assert.Equal(["DisplayName", "GPOName"], names);
    }

    [Fact]
    public async Task Certs_tolerate_year_9999_expiry()
    {
        Route(_ => Ok(new
        {
            Thumbprint = "ABC123",
            Subject = "CN=self-signed",
            Issuer = "CN=self-signed",
            NotAfter = new DateTime(9999, 12, 31, 23, 59, 59),
        }));

        var result = await Collector.CollectAsync("srv1", ["certs"], CancellationToken.None);

        var cert = Assert.Single(result.Snapshot.Certs!);
        Assert.Equal(9999, cert.NotAfter!.Value.Year);
    }

    [Fact]
    public async Task Sections_are_capped_at_the_hard_row_limit()
    {
        Route(_ => Ok(Enumerable.Range(0, InventoryCollector.MaxRowsPerSection + 50)
            .Select(object (i) => new { Name = $"svc{i:D4}" }).ToArray()));

        var result = await Collector.CollectAsync("srv1", ["services"], CancellationToken.None);

        Assert.Equal(InventoryCollector.MaxRowsPerSection, result.Snapshot.Services!.Count);
        Assert.True(result.Outcomes.Single().Truncated);
    }

    [Fact]
    public async Task Unreachable_server_stops_remaining_sections()
    {
        Route(_ => new ServerCallResult { Server = "srv1", Status = ServerCallStatus.Unreachable, Error = "timeout" });

        var result = await Collector.CollectAsync(
            "srv1", ["services", "software", "hotfix"], CancellationToken.None);

        Assert.Single(_sentCommands); // no pointless retries against a dead server
        var outcome = Assert.Single(result.Outcomes);
        Assert.True(outcome.Unreachable);
    }

    [Fact]
    public async Task Partial_registry_output_with_errors_still_maps()
    {
        // Typical: WOW6432Node hive missing → non-terminating error + 64-bit output.
        Route(_ => new ServerCallResult
        {
            Server = "srv1",
            Status = ServerCallStatus.Error,
            Error = "Cannot find path WOW6432Node",
            Output = [PSObject.AsPSObject(new { DisplayName = "App" })],
        });

        var result = await Collector.CollectAsync("srv1", ["software"], CancellationToken.None);

        Assert.Single(result.Snapshot.Software!);
        Assert.Null(result.Outcomes.Single().Error);
    }

    [Fact]
    public async Task Failed_section_does_not_stop_the_next_one()
    {
        Route(c => c.CommandName == FleetCommands.GetService
            ? new ServerCallResult { Server = "srv1", Status = ServerCallStatus.Error, Error = "denied" }
            : Ok(new { HotFixID = "KB500001", InstalledOn = new DateTime(2026, 5, 1) }));

        var result = await Collector.CollectAsync("srv1", ["services", "hotfix"], CancellationToken.None);

        Assert.Equal("denied", result.Outcomes[0].Error);
        Assert.Null(result.Snapshot.Services);
        var hotfix = Assert.Single(result.Snapshot.Hotfix!);
        Assert.Equal("KB500001", hotfix.Id);
        Assert.Equal(new DateOnly(2026, 5, 1), hotfix.InstalledOn);
    }

    [Fact]
    public async Task Malformed_property_values_do_not_fail_the_section()
    {
        Route(_ => Ok(
            new { HotFixID = "KB1", InstalledOn = "not a date at all" },
            new { HotFixID = "KB2", InstalledOn = new DateTime(2026, 6, 1) }));

        var result = await Collector.CollectAsync("srv1", ["hotfix"], CancellationToken.None);

        var hotfixes = result.Snapshot.Hotfix!;
        Assert.Equal(2, hotfixes.Count);
        Assert.Null(hotfixes[0].InstalledOn); // garbage tolerated, row kept
        Assert.NotNull(hotfixes[1].InstalledOn);
    }

    [Fact]
    public async Task Unknown_section_is_rejected()
        => await Assert.ThrowsAsync<ArgumentException>(
            () => Collector.CollectAsync("srv1", ["registry"], CancellationToken.None));

    [Theory]
    [InlineData(1, true)]      // year 1 + positive offset would underflow
    [InlineData(9999, true)]   // year 9999 + negative offset would overflow
    [InlineData(2026, false)]
    public void Extreme_dates_are_pinned_to_utc(int year, bool pinnedUtc)
    {
        var value = new DateTime(year, 6, 15, 12, 0, 0);

        var offset = InventoryCollector.ToSafeOffset(value)!.Value;

        Assert.Equal(year, offset.Year);
        if (pinnedUtc)
            Assert.Equal(TimeSpan.Zero, offset.Offset);
    }
}
