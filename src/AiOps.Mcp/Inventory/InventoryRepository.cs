using System.Data;
using System.Text.Json;
using AiOps.Contracts;
using DbUp;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace AiOps.Mcp.Inventory;

public interface IInventoryRepository
{
    Task UpsertAsync(InventorySnapshot snapshot, CancellationToken cancellationToken);
}

/// <summary>
/// Persists one snapshot row per server (MERGE upsert, no history). Migrations are
/// applied once per process via DbUp from embedded Db/Scripts/*.sql.
/// </summary>
public sealed class SqlInventoryRepository(
    IOptions<InventoryOptions> options,
    ILogger<SqlInventoryRepository> logger) : IInventoryRepository
{
    private readonly string _connectionString = options.Value.SqlConnectionString
        ?? throw new InvalidOperationException("Inventory:SqlConnectionString is not configured.");

    private readonly SemaphoreSlim _migrationGate = new(1, 1);
    private volatile bool _migrated;

    /// <summary>
    /// How long a failed migration is remembered. DbUp's check and upgrade are synchronous and
    /// each opens its own connection, so without this every server in the nightly sweep would
    /// retry the whole migration behind the gate: with the database down, 500 servers pay the
    /// connect timeout one after another and a sweep that should fail in seconds runs for hours.
    /// </summary>
    private static readonly TimeSpan MigrationFailureCooldown = TimeSpan.FromMinutes(1);

    private DateTimeOffset _migrationFailedAt = DateTimeOffset.MinValue;
    private Exception? _migrationFailure;

    internal const string UpsertSql =
        """
        MERGE dbo.ServerInventory WITH (HOLDLOCK) AS target
        USING (SELECT @ServerName AS ServerName) AS source
        ON target.ServerName = source.ServerName
        WHEN MATCHED THEN UPDATE SET
            CollectedAtUtc = @CollectedAtUtc,
            -- ISNULL: a section that FAILED to collect arrives as NULL and must keep the
            -- previous good JSON rather than erase it (an empty-but-collected section is '[]').
            Services = ISNULL(@Services, target.Services),
            Software = ISNULL(@Software, target.Software),
            Ports    = ISNULL(@Ports,    target.Ports),
            Gpo      = ISNULL(@Gpo,      target.Gpo),
            Hotfix   = ISNULL(@Hotfix,   target.Hotfix),
            Certs    = ISNULL(@Certs,    target.Certs),
            Tasks    = ISNULL(@Tasks,    target.Tasks)
        WHEN NOT MATCHED THEN INSERT
            (ServerName, CollectedAtUtc, Services, Software, Ports, Gpo, Hotfix, Certs, Tasks)
        VALUES
            (@ServerName, @CollectedAtUtc, @Services, @Software, @Ports, @Gpo, @Hotfix, @Certs, @Tasks);
        """;

    public async Task UpsertAsync(InventorySnapshot snapshot, CancellationToken cancellationToken)
    {
        await EnsureMigratedAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = UpsertSql;
        // Explicitly typed parameters: AddWithValue infers nvarchar(<actual length>) per call,
        // which makes every distinct JSON size a new entry in SQL Server's plan cache.
        command.Parameters.Add(new SqlParameter("@ServerName", SqlDbType.NVarChar, 255) { Value = snapshot.Server });
        command.Parameters.Add(new SqlParameter("@CollectedAtUtc", SqlDbType.DateTime2) { Value = snapshot.CollectedAt.UtcDateTime });
        AddJsonParameter(command, "@Services", snapshot.Services);
        AddJsonParameter(command, "@Software", snapshot.Software);
        AddJsonParameter(command, "@Ports", snapshot.Ports);
        AddJsonParameter(command, "@Gpo", snapshot.Gpo);
        AddJsonParameter(command, "@Hotfix", snapshot.Hotfix);
        AddJsonParameter(command, "@Certs", snapshot.Certs);
        AddJsonParameter(command, "@Tasks", snapshot.Tasks);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void AddJsonParameter<T>(SqlCommand command, string name, IReadOnlyList<T>? items)
        => command.Parameters.Add(new SqlParameter(name, SqlDbType.NVarChar, -1)
        {
            Value = items is null ? DBNull.Value : JsonSerializer.Serialize(items, JsonSerializerOptions.Web),
        });

    private async Task EnsureMigratedAsync(CancellationToken cancellationToken)
    {
        if (_migrated)
            return;

        await _migrationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_migrated)
                return;

            // Inside the gate so the cooldown is read and written consistently.
            if (_migrationFailure is { } recent && DateTimeOffset.UtcNow - _migrationFailedAt < MigrationFailureCooldown)
                throw new InvalidOperationException(
                    "Inventory schema migration failed recently and is in cooldown; not retried for every server.", recent);

            var upgrader = DeployChanges.To
                .SqlDatabase(_connectionString)
                .WithScriptsEmbeddedInAssembly(
                    typeof(SqlInventoryRepository).Assembly,
                    name => name.Contains(".Db.Scripts.", StringComparison.Ordinal))
                .WithTransactionPerScript()
                .LogToNowhere()
                .Build();

            if (upgrader.IsUpgradeRequired())
            {
                var result = upgrader.PerformUpgrade();
                if (!result.Successful)
                    throw new InvalidOperationException("Inventory schema migration failed.", result.Error);

                logger.LogInformation("Applied {Count} inventory schema migration(s)", result.Scripts.Count());
            }

            _migrated = true;
            _migrationFailure = null;
        }
        catch (Exception ex)
        {
            _migrationFailure = ex;
            _migrationFailedAt = DateTimeOffset.UtcNow;
            throw;
        }
        finally
        {
            _migrationGate.Release();
        }
    }
}
