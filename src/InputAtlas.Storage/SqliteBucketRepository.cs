using System.Globalization;
using System.Text.Json;
using InputAtlas.Core;
using Microsoft.Data.Sqlite;

namespace InputAtlas.Storage;

public sealed class SqliteBucketRepository : IBucketRepository
{
    public const int CurrentSchemaVersion = 1;
    private readonly string _databasePath;
    private readonly string _connectionString;
    private bool _initialized;

    public SqliteBucketRepository(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        _databasePath = Path.GetFullPath(databasePath);
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
            DefaultTimeout = 2,
        };
        _connectionString = builder.ToString();
    }

    public string DatabasePath => _databasePath;

    public async ValueTask InitializeAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_databasePath)!);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS bucket_5m (
                bucket_start_utc INTEGER PRIMARY KEY,
                coverage_seconds INTEGER NOT NULL CHECK (coverage_seconds BETWEEN 0 AND 300),
                counts_json TEXT NOT NULL,
                updated_utc INTEGER NOT NULL
            );
            CREATE TABLE IF NOT EXISTS bucket_1h (
                bucket_start_utc INTEGER PRIMARY KEY,
                coverage_seconds INTEGER NOT NULL CHECK (coverage_seconds BETWEEN 0 AND 3600),
                counts_json TEXT NOT NULL,
                updated_utc INTEGER NOT NULL
            );
            CREATE TABLE IF NOT EXISTS metadata (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS migration_history (
                version INTEGER PRIMARY KEY,
                applied_utc INTEGER NOT NULL
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        var versionCommand = connection.CreateCommand();
        versionCommand.Transaction = transaction;
        versionCommand.CommandText = "SELECT value FROM metadata WHERE key = 'schema_version';";
        var existing = (string?)await versionCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (existing is not null &&
            (!int.TryParse(existing, NumberStyles.None, CultureInfo.InvariantCulture, out var version) ||
             version > CurrentSchemaVersion))
        {
            throw new NotSupportedException("数据库模式版本高于当前应用支持范围。");
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var metadata = connection.CreateCommand();
        metadata.Transaction = transaction;
        metadata.CommandText = """
            INSERT INTO metadata(key, value) VALUES ('schema_version', $version)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;
            INSERT INTO migration_history(version, applied_utc) VALUES ($versionNumber, $now)
            ON CONFLICT(version) DO NOTHING;
            """;
        metadata.Parameters.AddWithValue("$version", CurrentSchemaVersion.ToString(CultureInfo.InvariantCulture));
        metadata.Parameters.AddWithValue("$versionNumber", CurrentSchemaVersion);
        metadata.Parameters.AddWithValue("$now", now);
        await metadata.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        _initialized = true;
    }

    public async ValueTask UpsertFiveMinuteAsync(
        BucketSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        if (snapshot.BucketStartUtc != TimeBuckets.AlignFiveMinutes(snapshot.BucketStartUtc))
        {
            throw new ArgumentException("5 分钟桶起始时间未按 UTC 边界对齐。", nameof(snapshot));
        }

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO bucket_5m(bucket_start_utc, coverage_seconds, counts_json, updated_utc)
            VALUES ($start, $coverage, $counts, $updated)
            ON CONFLICT(bucket_start_utc) DO UPDATE SET
                coverage_seconds = excluded.coverage_seconds,
                counts_json = excluded.counts_json,
                updated_utc = excluded.updated_utc;
            """;
        command.Parameters.AddWithValue("$start", snapshot.BucketStartUtc);
        command.Parameters.AddWithValue("$coverage", Math.Clamp(snapshot.CoverageSeconds, 0, 300));
        command.Parameters.AddWithValue("$counts", SerializeCounts(snapshot.Counts));
        command.Parameters.AddWithValue("$updated", snapshot.UpdatedUtc);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<IReadOnlyList<BucketSnapshot>> ReadRangeAsync(
        long startUtc,
        long endUtc,
        CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        if (endUtc <= startUtc)
        {
            return [];
        }

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var result = new SortedDictionary<long, BucketSnapshot>();

        var details = connection.CreateCommand();
        details.CommandText = """
            SELECT bucket_start_utc, coverage_seconds, counts_json, updated_utc
            FROM bucket_5m
            WHERE bucket_start_utc < $end AND bucket_start_utc + 300 > $start
            ORDER BY bucket_start_utc;
            """;
        details.Parameters.AddWithValue("$start", startUtc);
        details.Parameters.AddWithValue("$end", endUtc);
        await using (var reader = await details.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var snapshot = ReadSnapshot(reader);
                result[snapshot.BucketStartUtc] = snapshot;
            }
        }

        var archived = connection.CreateCommand();
        archived.CommandText = """
            SELECT bucket_start_utc, coverage_seconds, counts_json, updated_utc
            FROM bucket_1h
            WHERE bucket_start_utc < $end AND bucket_start_utc + 3600 > $start
            ORDER BY bucket_start_utc;
            """;
        archived.Parameters.AddWithValue("$start", startUtc);
        archived.Parameters.AddWithValue("$end", endUtc);
        await using (var reader = await archived.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var snapshot = ReadSnapshot(reader);
                var hourHasDetails = result.Keys.Any(key => TimeBuckets.AlignHour(key) == snapshot.BucketStartUtc);
                if (!hourHasDetails)
                {
                    result[snapshot.BucketStartUtc] = snapshot;
                }
            }
        }

        return result.Values.ToArray();
    }

    public async ValueTask<string?> GetMetadataAsync(
        string key,
        CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM metadata WHERE key = $key;";
        command.Parameters.AddWithValue("$key", key);
        return (string?)await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask SetMetadataAsync(
        string key,
        string value,
        CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO metadata(key, value) VALUES ($key, $value)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;
            """;
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<bool> IntegrityCheckAsync(CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var command = connection.CreateCommand();
        command.CommandText = "PRAGMA quick_check;";
        return string.Equals(
            (string?)await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            "ok",
            StringComparison.OrdinalIgnoreCase);
    }

    public async ValueTask<int> DeleteRangeAsync(
        long startUtc,
        long endUtc,
        CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        if (endUtc <= startUtc)
        {
            return 0;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DELETE FROM bucket_5m WHERE bucket_start_utc < $end AND bucket_start_utc + 300 > $start;
            DELETE FROM bucket_1h WHERE bucket_start_utc < $end AND bucket_start_utc + 3600 > $start;
            """;
        command.Parameters.AddWithValue("$start", startUtc);
        command.Parameters.AddWithValue("$end", endUtc);
        var affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return affected;
    }

    public async ValueTask<int> CompactBeforeAsync(
        long cutoffUtc,
        CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        var cutoffHour = TimeBuckets.AlignHour(cutoffUtc);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var candidateCommand = connection.CreateCommand();
        candidateCommand.CommandText = """
            SELECT DISTINCT (bucket_start_utc / 3600) * 3600
            FROM bucket_5m
            WHERE bucket_start_utc < $cutoff
            ORDER BY 1;
            """;
        candidateCommand.Parameters.AddWithValue("$cutoff", cutoffHour);
        var hours = new List<long>();
        await using (var reader = await candidateCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                hours.Add(reader.GetInt64(0));
            }
        }

        var compacted = 0;
        foreach (var hour in hours)
        {
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            var read = connection.CreateCommand();
            read.Transaction = transaction;
            read.CommandText = """
                SELECT coverage_seconds, counts_json
                FROM bucket_5m
                WHERE bucket_start_utc >= $start AND bucket_start_utc < $end
                ORDER BY bucket_start_utc;
                """;
            read.Parameters.AddWithValue("$start", hour);
            read.Parameters.AddWithValue("$end", hour + 3600);
            var coverage = 0;
            var counts = new SortedDictionary<InputId, long>();
            await using (var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    coverage = Math.Min(3600, coverage + reader.GetInt32(0));
                    foreach (var pair in DeserializeCounts(reader.GetString(1)))
                    {
                        counts.TryGetValue(pair.Key, out var current);
                        counts[pair.Key] = current > long.MaxValue - pair.Value ? long.MaxValue : current + pair.Value;
                    }
                }
            }

            var upsert = connection.CreateCommand();
            upsert.Transaction = transaction;
            upsert.CommandText = """
                INSERT INTO bucket_1h(bucket_start_utc, coverage_seconds, counts_json, updated_utc)
                VALUES ($start, $coverage, $counts, $updated)
                ON CONFLICT(bucket_start_utc) DO UPDATE SET
                    coverage_seconds = excluded.coverage_seconds,
                    counts_json = excluded.counts_json,
                    updated_utc = excluded.updated_utc;
                """;
            upsert.Parameters.AddWithValue("$start", hour);
            upsert.Parameters.AddWithValue("$coverage", coverage);
            upsert.Parameters.AddWithValue("$counts", SerializeCounts(counts));
            upsert.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            await upsert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            var verify = connection.CreateCommand();
            verify.Transaction = transaction;
            verify.CommandText = "SELECT coverage_seconds, counts_json FROM bucket_1h WHERE bucket_start_utc = $start;";
            verify.Parameters.AddWithValue("$start", hour);
            await using (var reader = await verify.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ||
                    reader.GetInt32(0) != coverage ||
                    !string.Equals(reader.GetString(1), SerializeCounts(counts), StringComparison.Ordinal))
                {
                    throw new InvalidDataException("小时桶回读校验失败。");
                }
            }

            var delete = connection.CreateCommand();
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM bucket_5m WHERE bucket_start_utc >= $start AND bucket_start_utc < $end;";
            delete.Parameters.AddWithValue("$start", hour);
            delete.Parameters.AddWithValue("$end", hour + 3600);
            await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            compacted++;
        }

        if (compacted > 0)
        {
            await SetMetadataAsync(
                "last_compaction_utc",
                DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
                cancellationToken).ConfigureAwait(false);
        }

        return compacted;
    }

    public async ValueTask CreateConsistentBackupAsync(
        string destinationPath,
        CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        var fullDestination = Path.GetFullPath(destinationPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullDestination)!);
        if (File.Exists(fullDestination))
        {
            File.Delete(fullDestination);
        }

        await using var source = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var destinationBuilder = new SqliteConnectionStringBuilder
        {
            DataSource = fullDestination,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        };
        await using var destination = new SqliteConnection(destinationBuilder.ToString());
        await destination.OpenAsync(cancellationToken).ConfigureAwait(false);
        source.BackupDatabase(destination);
    }

    public ValueTask DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        return ValueTask.CompletedTask;
    }

    private async ValueTask<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA foreign_keys=ON; PRAGMA busy_timeout=2000;";
        await pragma.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private static BucketSnapshot ReadSnapshot(SqliteDataReader reader) => new(
        reader.GetInt64(0),
        reader.GetInt32(1),
        DeserializeCounts(reader.GetString(2)),
        reader.GetInt64(3));

    private static string SerializeCounts(IReadOnlyDictionary<InputId, long> counts)
    {
        var stable = new SortedDictionary<string, long>(StringComparer.Ordinal);
        foreach (var pair in counts.OrderBy(static item => item.Key.Value))
        {
            if (pair.Value > 0)
            {
                stable[pair.Key.ToString()] = pair.Value;
            }
        }

        return JsonSerializer.Serialize(stable);
    }

    private static SortedDictionary<InputId, long> DeserializeCounts(string json)
    {
        var raw = JsonSerializer.Deserialize<Dictionary<string, long>>(json) ?? [];
        var result = new SortedDictionary<InputId, long>();
        foreach (var pair in raw)
        {
            if (ushort.TryParse(pair.Key, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && pair.Value > 0)
            {
                result[new InputId(id)] = pair.Value;
            }
        }

        return result;
    }

    private void EnsureInitialized()
    {
        if (!_initialized)
        {
            throw new InvalidOperationException("数据库尚未初始化。");
        }
    }
}
