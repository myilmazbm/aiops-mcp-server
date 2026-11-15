using AiOps.Mcp.Inventory;
using AiOps.Mcp.Remoting;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AiOps.Mcp.LabTests;

/// <summary>
/// Full nightly-sweep path against real infrastructure: collect all inventory sections
/// from AIOPS_LAB_SERVER over JEA, upsert into the real AiOpsInventory database
/// (AIOPS_SQL_CONNECTION), then read the row back. Skipped unless both are set. Not run in CI.
/// </summary>
public class EndToEndLabTests
{
    private static string? LabServer => Environment.GetEnvironmentVariable("AIOPS_LAB_SERVER");
    private static string? SqlConnection => Environment.GetEnvironmentVariable("AIOPS_SQL_CONNECTION");

    [SkippableFact]
    public async Task Collects_from_a_real_server_and_upserts_into_the_real_database()
    {
        Skip.If(string.IsNullOrEmpty(LabServer), "AIOPS_LAB_SERVER not set");
        Skip.If(string.IsNullOrEmpty(SqlConnection), "AIOPS_SQL_CONNECTION not set");

        var remoting = Options.Create(new RemotingOptions());
        using var pool = new PsSessionPool(new WsmanRunspaceConnector(remoting), remoting, NullLogger<PsSessionPool>.Instance);
        var executor = new JeaRemoteExecutor(pool, remoting, NullLogger<JeaRemoteExecutor>.Instance);
        var collector = new InventoryCollector(executor);

        var result = await collector.CollectAsync(LabServer!, InventoryCollector.AllSections, CancellationToken.None);
        Assert.DoesNotContain(result.Outcomes, o => o.Unreachable);
        Assert.NotEmpty(result.Snapshot.Services!);
        Assert.NotEmpty(result.Snapshot.Software!);
        Assert.NotEmpty(result.Snapshot.Ports!);
        Assert.NotEmpty(result.Snapshot.Hotfix!);

        var repository = new SqlInventoryRepository(
            Options.Create(new InventoryOptions { SqlConnectionString = SqlConnection }),
            NullLogger<SqlInventoryRepository>.Instance);
        await repository.UpsertAsync(result.Snapshot, CancellationToken.None);
        // Second upsert proves MERGE-update against the real schema (still one row).
        await repository.UpsertAsync(result.Snapshot, CancellationToken.None);

        await using var connection = new SqlConnection(SqlConnection);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        // ISNULL per column: a single failed section (NULL column) must not void the sum.
        command.CommandText =
            "SELECT COUNT(*), MAX(ISNULL(DATALENGTH(Services),0) + ISNULL(DATALENGTH(Software),0) + " +
            "ISNULL(DATALENGTH(Ports),0) + ISNULL(DATALENGTH(Gpo),0) + ISNULL(DATALENGTH(Hotfix),0) + " +
            "ISNULL(DATALENGTH(Certs),0) + ISNULL(DATALENGTH(Tasks),0)) " +
            "FROM dbo.ServerInventory WHERE ServerName = @s";
        command.Parameters.AddWithValue("@s", LabServer);
        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();

        Assert.Equal(1, reader.GetInt32(0)); // upsert, not insert-twice
        var snapshotBytes = reader.GetInt64(1);
        Assert.InRange(snapshotBytes, 1, 400_000); // stays near the ~100 KB budget
    }
}
