using AiOps.Contracts;
using AiOps.Mcp.Inventory;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AiOps.Mcp.LabTests;

/// <summary>
/// Real-database tests for the inventory repository (DbUp migration + MERGE upsert)
/// against SQL Server LocalDB. Skipped when LocalDB is not installed. Not run in CI.
/// </summary>
public sealed class SqlInventoryRepositoryTests : IDisposable
{
    private const string MasterConnectionString =
        @"Server=(localdb)\MSSQLLocalDB;Database=master;Integrated Security=true";
    private const string DatabaseName = "AiOpsInventoryLabTest";
    private const string ConnectionString =
        $@"Server=(localdb)\MSSQLLocalDB;Database={DatabaseName};Integrated Security=true";

    private static bool LocalDbAvailable
    {
        get
        {
            try
            {
                using var connection = new SqlConnection(MasterConnectionString);
                connection.Open();
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    private static SqlInventoryRepository CreateRepository()
        => new(
            Options.Create(new InventoryOptions { SqlConnectionString = ConnectionString }),
            NullLogger<SqlInventoryRepository>.Instance);

    private static void RunOnMaster(string sql)
    {
        using var connection = new SqlConnection(MasterConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    public SqlInventoryRepositoryTests()
    {
        if (!LocalDbAvailable)
            return;

        RunOnMaster($"""
            IF DB_ID(N'{DatabaseName}') IS NULL
                CREATE DATABASE {DatabaseName} COLLATE SQL_Latin1_General_CP1_CI_AS;
            """);
    }

    public void Dispose()
    {
        if (!LocalDbAvailable)
            return;

        SqlConnection.ClearAllPools();
        RunOnMaster($"""
            IF DB_ID(N'{DatabaseName}') IS NOT NULL
            BEGIN
                ALTER DATABASE {DatabaseName} SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                DROP DATABASE {DatabaseName};
            END
            """);
    }

    private static InventorySnapshot Snapshot(string server, string serviceName) => new()
    {
        Server = server,
        CollectedAt = DateTimeOffset.UtcNow,
        Services = [new ServiceDto { Name = serviceName, Status = "Running" }],
        Software = [new SoftwareDto { Name = "App", Version = "1.0" }],
    };

    private static (int Rows, string? ServicesJson) ReadRow(string server)
    {
        using var connection = new SqlConnection(ConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*), MAX(Services) FROM dbo.ServerInventory WHERE ServerName = @s";
        command.Parameters.AddWithValue("@s", server);
        using var reader = command.ExecuteReader();
        reader.Read();
        return (reader.GetInt32(0), reader.IsDBNull(1) ? null : reader.GetString(1));
    }

    [SkippableFact]
    public async Task Migration_and_upsert_round_trip()
    {
        Skip.IfNot(LocalDbAvailable, "LocalDB not available");
        var repository = CreateRepository();

        // Insert path (also applies the DbUp migration on first touch)
        await repository.UpsertAsync(Snapshot("SRV-A", "WinRM"), CancellationToken.None);
        var (rows, json) = ReadRow("SRV-A");
        Assert.Equal(1, rows);
        Assert.Contains("WinRM", json);

        // Update path: still one row, content replaced, no history
        await repository.UpsertAsync(Snapshot("SRV-A", "Spooler"), CancellationToken.None);
        (rows, json) = ReadRow("SRV-A");
        Assert.Equal(1, rows);
        Assert.Contains("Spooler", json);
        Assert.DoesNotContain("WinRM", json);
    }

    [SkippableFact]
    public async Task Concurrent_upserts_for_the_same_server_do_not_violate_the_primary_key()
    {
        Skip.IfNot(LocalDbAvailable, "LocalDB not available");
        var repository = CreateRepository();

        // Without HOLDLOCK on the MERGE this races into duplicate-key errors.
        await Task.WhenAll(Enumerable.Range(0, 20).Select(i =>
            repository.UpsertAsync(Snapshot("SRV-RACE", $"svc{i}"), CancellationToken.None)));

        var (rows, _) = ReadRow("SRV-RACE");
        Assert.Equal(1, rows);
    }

    [SkippableFact]
    public async Task A_failed_section_keeps_the_previous_good_value_on_update()
    {
        Skip.IfNot(LocalDbAvailable, "LocalDB not available");
        var repository = CreateRepository();

        await repository.UpsertAsync(Snapshot("SRV-KEEP", "WinRM"), CancellationToken.None);

        // Next sweep: Services failed to collect (null). The MERGE must keep yesterday's JSON.
        await repository.UpsertAsync(
            new InventorySnapshot { Server = "SRV-KEEP", CollectedAt = DateTimeOffset.UtcNow, Software = [] },
            CancellationToken.None);

        var (rows, json) = ReadRow("SRV-KEEP");
        Assert.Equal(1, rows);
        Assert.Contains("WinRM", json); // not erased by the NULL

        // An empty-but-collected section ('[]') DOES overwrite.
        await repository.UpsertAsync(
            new InventorySnapshot { Server = "SRV-KEEP", CollectedAt = DateTimeOffset.UtcNow, Services = [] },
            CancellationToken.None);
        (_, json) = ReadRow("SRV-KEEP");
        Assert.Equal("[]", json);
    }

    [SkippableFact]
    public async Task Sections_not_collected_are_stored_as_null()
    {
        Skip.IfNot(LocalDbAvailable, "LocalDB not available");
        var repository = CreateRepository();

        await repository.UpsertAsync(
            new InventorySnapshot { Server = "SRV-NULLS", CollectedAt = DateTimeOffset.UtcNow },
            CancellationToken.None);

        var (rows, json) = ReadRow("SRV-NULLS");
        Assert.Equal(1, rows);
        Assert.Null(json);
    }
}
