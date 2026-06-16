using System.Text.Json;
using EdgeObservabilityAgent.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace EdgeObservabilityAgent.Services;

/// <summary>
/// SQLite-backed buffer that persists metric payloads when the HQ link is down.
/// Uses WAL mode for safe concurrent access between the collection loop and the
/// resync background service.
/// </summary>
public sealed class BufferRepository
{
    private readonly ILogger<BufferRepository> _logger;
    private const string DbPath = "edge_buffer.db";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public BufferRepository(ILogger<BufferRepository> logger)
    {
        _logger = logger;
    }

    // ---------------------------------------------------------------------------
    // Initialisation
    // ---------------------------------------------------------------------------

    public async Task InitAsync(CancellationToken ct = default)
    {
        await using var conn = OpenConnection();

        // Enable WAL mode for safe concurrent reads/writes.
        await using (var pragma = conn.CreateCommand())
        {
            pragma.CommandText = "PRAGMA journal_mode=WAL;";
            await pragma.ExecuteNonQueryAsync(ct);
        }

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS MetricBuffer (
                Id        INTEGER PRIMARY KEY AUTOINCREMENT,
                Timestamp TEXT    NOT NULL,
                Payload   BLOB    NOT NULL,
                Synced    INTEGER NOT NULL DEFAULT 0
            );
            CREATE INDEX IF NOT EXISTS idx_synced ON MetricBuffer(Synced);
            """;
        await cmd.ExecuteNonQueryAsync(ct);

        _logger.LogInformation("BufferRepository initialised at {DbPath}", DbPath);
    }

    // ---------------------------------------------------------------------------
    // Write
    // ---------------------------------------------------------------------------

    public async Task SaveAsync(MetricPayload payload, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(payload, JsonOpts);
        var bytes = System.Text.Encoding.UTF8.GetBytes(json);

        await using var conn = OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO MetricBuffer (Timestamp, Payload, Synced)
            VALUES ($ts, $payload, 0);
            """;
        cmd.Parameters.AddWithValue("$ts", payload.Timestamp.ToString("O"));
        cmd.Parameters.AddWithValue("$payload", bytes);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ---------------------------------------------------------------------------
    // Read
    // ---------------------------------------------------------------------------

    public async Task<List<(long Id, MetricPayload Payload)>> GetPendingAsync(
        int count, CancellationToken ct = default)
    {
        var results = new List<(long, MetricPayload)>();

        await using var conn = OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT Id, Payload
            FROM MetricBuffer
            WHERE Synced = 0
            ORDER BY Id ASC
            LIMIT $count;
            """;
        cmd.Parameters.AddWithValue("$count", count);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var id = reader.GetInt64(0);
            var bytes = (byte[])reader.GetValue(1);
            var json = System.Text.Encoding.UTF8.GetString(bytes);

            var payload = JsonSerializer.Deserialize<MetricPayload>(json, JsonOpts);
            if (payload is not null)
                results.Add((id, payload));
        }

        return results;
    }

    public async Task<int> GetPendingCountAsync(CancellationToken ct = default)
    {
        await using var conn = OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM MetricBuffer WHERE Synced = 0;";
        var result = await cmd.ExecuteScalarAsync(ct);
        return Convert.ToInt32(result);
    }

    // ---------------------------------------------------------------------------
    // Mark synced
    // ---------------------------------------------------------------------------

    public async Task MarkSyncedAsync(IEnumerable<long> ids, CancellationToken ct = default)
    {
        var idList = ids.ToList();
        if (idList.Count == 0) return;

        // Build a parameterised IN clause.
        var paramNames = idList.Select((_, i) => $"$id{i}").ToList();
        var inClause = string.Join(", ", paramNames);

        await using var conn = OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"UPDATE MetricBuffer SET Synced = 1 WHERE Id IN ({inClause});";

        for (var i = 0; i < idList.Count; i++)
            cmd.Parameters.AddWithValue(paramNames[i], idList[i]);

        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ---------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------

    private static SqliteConnection OpenConnection()
    {
        var conn = new SqliteConnection($"Data Source={DbPath}");
        conn.Open();
        return conn;
    }
}
