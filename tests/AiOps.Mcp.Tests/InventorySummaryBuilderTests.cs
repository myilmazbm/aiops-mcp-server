using AiOps.Contracts;
using AiOps.Mcp.Inventory;

namespace AiOps.Mcp.Tests;

public class InventorySummaryBuilderTests
{
    private static InventorySnapshot Empty(string server = "SRV1")
        => new() { Server = server, CollectedAt = DateTimeOffset.UtcNow };

    [Fact]
    public void Empty_snapshot_produces_minimal_valid_summary()
    {
        var summary = InventorySummaryBuilder.Build(Empty());

        Assert.StartsWith("server: SRV1", summary);
        Assert.DoesNotContain("services", summary);
    }

    [Fact]
    public void Summary_covers_all_populated_sections()
    {
        var snapshot = Empty() with
        {
            Services = [new ServiceDto { Name = "MSSQLSERVER", DisplayName = "SQL Server (MSSQLSERVER)", Status = "Running" }],
            Software = [new SoftwareDto { Name = "Microsoft SQL Server 2022", Version = "16.0.4135.4" }],
            Ports = [new ListeningPortDto { Port = 1433, Address = "0.0.0.0", ProcessName = "sqlservr" }],
            Gpo = [new GpoDto { Name = "Baseline Policy" }],
            Hotfix = [new HotfixDto { Id = "KB5044281", InstalledOn = new DateOnly(2026, 6, 1) }],
            Certs = [new CertificateDto { Thumbprint = "ABC", Subject = "CN=sql01", NotAfter = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero) }],
            Tasks = [new ScheduledTaskDto { Name = "IndexMaintenance" }],
        };

        var summary = InventorySummaryBuilder.Build(snapshot);

        Assert.Contains("SQL Server (MSSQLSERVER)", summary);
        Assert.Contains("Microsoft SQL Server 2022 16.0.4135.4", summary);
        Assert.Contains("1433 (sqlservr)", summary);
        Assert.Contains("Baseline Policy", summary);
        Assert.Contains("KB5044281", summary);
        Assert.Contains("CN=sql01 (expires 2026-09-01)", summary);
        Assert.Contains("IndexMaintenance", summary);
        Assert.Contains("services: 1 running of 1 total", summary);
    }

    [Fact]
    public void Summary_is_deterministic_regardless_of_input_order()
    {
        var a = new ServiceDto { Name = "A", DisplayName = "Alpha", Status = "Running" };
        var b = new ServiceDto { Name = "B", DisplayName = "Beta", Status = "Running" };
        var certX = new CertificateDto { Thumbprint = "X", Subject = "CN=x" };
        var certY = new CertificateDto { Thumbprint = "Y", Subject = "CN=y" };
        var taskP = new ScheduledTaskDto { Name = "Patch" };
        var taskQ = new ScheduledTaskDto { Name = "Backup" };

        var one = InventorySummaryBuilder.Build(Empty() with { Services = [a, b], Certs = [certX, certY], Tasks = [taskP, taskQ] });
        var two = InventorySummaryBuilder.Build(Empty() with { Services = [b, a], Certs = [certY, certX], Tasks = [taskQ, taskP] });

        // Same fleet state must yield the same text, hence a stable embedding vector.
        Assert.Equal(one, two);
    }

    [Fact]
    public void Same_date_hotfixes_produce_a_stable_ordering()
    {
        var date = new DateOnly(2026, 6, 10); // one Patch-Tuesday date shared by all
        var h1 = new HotfixDto { Id = "KB5000001", InstalledOn = date };
        var h2 = new HotfixDto { Id = "KB5000002", InstalledOn = date };
        var h3 = new HotfixDto { Id = "KB5000003", InstalledOn = date };

        var one = InventorySummaryBuilder.Build(Empty() with { Hotfix = [h1, h2, h3] });
        var two = InventorySummaryBuilder.Build(Empty() with { Hotfix = [h3, h1, h2] });

        Assert.Equal(one, two); // WMI enumeration order must not leak into the summary
    }

    [Fact]
    public void Same_port_listeners_produce_a_stable_ordering()
    {
        var p1 = new ListeningPortDto { Port = 443, Address = "0.0.0.0", ProcessName = "svcA" };
        var p2 = new ListeningPortDto { Port = 443, Address = "::", ProcessName = "svcB" };

        var one = InventorySummaryBuilder.Build(Empty() with { Ports = [p1, p2] });
        var two = InventorySummaryBuilder.Build(Empty() with { Ports = [p2, p1] });

        Assert.Equal(one, two);
    }

    [Fact]
    public void Stopped_services_are_counted_but_not_listed()
    {
        var snapshot = Empty() with
        {
            Services =
            [
                new ServiceDto { Name = "Run1", DisplayName = "RunningSvc", Status = "Running" },
                new ServiceDto { Name = "Stop1", DisplayName = "StoppedSvc", Status = "Stopped" },
            ],
        };

        var summary = InventorySummaryBuilder.Build(snapshot);

        Assert.Contains("services: 1 running of 2 total", summary);
        Assert.Contains("RunningSvc", summary);
        Assert.DoesNotContain("StoppedSvc", summary);
    }

    [Fact]
    public void Recent_hotfixes_are_newest_first_and_capped()
    {
        var hotfixes = Enumerable.Range(1, 30)
            .Select(i => new HotfixDto { Id = $"KB{i:D7}", InstalledOn = new DateOnly(2026, 1, 1).AddDays(i) })
            .ToList();

        var summary = InventorySummaryBuilder.Build(Empty() with { Hotfix = hotfixes });

        Assert.Contains("hotfixes: 30 installed", summary);
        Assert.Contains("KB0000030", summary); // newest kept
        Assert.DoesNotContain("KB0000005", summary); // beyond the top-20 window
    }

    [Fact]
    public void Output_never_exceeds_the_length_cap()
    {
        var software = Enumerable.Range(0, 400)
            .Select(i => new SoftwareDto { Name = new string('x', 200) + i })
            .ToList();

        var summary = InventorySummaryBuilder.Build(Empty() with { Software = software });

        Assert.True(summary.Length <= InventorySummaryBuilder.MaxLength);
    }
}
