using System.Globalization;
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using InputAtlas.Core;
using Microsoft.Data.Sqlite;

namespace InputAtlas.Storage;

public sealed class DataManagementService(SqliteBucketRepository repository, IApplicationLog log)
{
    private static readonly UTF8Encoding CsvEncoding = new(true);
    private static readonly JsonSerializerOptions ManifestJsonOptions = new() { WriteIndented = true };

    public async ValueTask<int> ExportCsvAsync(
        string destinationPath,
        long startUtc,
        long endUtc,
        TimeZoneInfo displayTimeZone,
        CancellationToken cancellationToken = default)
    {
        var started = Stopwatch.GetTimestamp();
        var fullPath = Path.GetFullPath(destinationPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var buckets = await repository.ReadRangeAsync(startUtc, endUtc, cancellationToken).ConfigureAwait(false);
        await using var stream = new FileStream(
            fullPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            8192,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await using var writer = new StreamWriter(stream, CsvEncoding);
        await writer.WriteLineAsync("bucket_start_utc,display_time,display_timezone,granularity,category,input_id,display_name,count,coverage_seconds,coverage_ratio").ConfigureAwait(false);
        var rows = 0;
        foreach (var bucket in buckets)
        {
            var utc = DateTimeOffset.FromUnixTimeSeconds(bucket.BucketStartUtc);
            var display = TimeZoneInfo.ConvertTime(utc, displayTimeZone);
            foreach (var pair in bucket.Counts.OrderBy(static item => item.Key.Value))
            {
                var category = MetricsCalculator.GetCategory(pair.Key);
                var coverageMaximum = bucket.BucketStartUtc == TimeBuckets.AlignHour(bucket.BucketStartUtc) && bucket.CoverageSeconds > 300
                    ? 3600
                    : 300;
                var values = new[]
                {
                    bucket.BucketStartUtc.ToString(CultureInfo.InvariantCulture),
                    display.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                    displayTimeZone.Id,
                    coverageMaximum == 3600 ? "1h" : "5m",
                    category.ToString(),
                    pair.Key.ToString(),
                    DisplayName(pair.Key),
                    pair.Value.ToString(CultureInfo.InvariantCulture),
                    bucket.CoverageSeconds.ToString(CultureInfo.InvariantCulture),
                    (bucket.CoverageSeconds / (double)coverageMaximum).ToString("0.####", CultureInfo.InvariantCulture),
                };
                await writer.WriteLineAsync(string.Join(',', values.Select(EscapeCsv))).ConfigureAwait(false);
                rows++;
            }
        }

        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        log.Information("export_completed", $"rows={rows} duration_ms={Stopwatch.GetElapsedTime(started).TotalMilliseconds:F1}");
        return rows;
    }

    public async ValueTask<string> CreateBackupPackageAsync(
        string destinationPath,
        string? settingsPath,
        string applicationVersion,
        CancellationToken cancellationToken = default)
    {
        var started = Stopwatch.GetTimestamp();
        var fullPath = Path.GetFullPath(destinationPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), "InputAtlasBackup", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryDirectory);
        try
        {
            var databaseBackup = Path.Combine(temporaryDirectory, "inputatlas.db");
            await repository.CreateConsistentBackupAsync(databaseBackup, cancellationToken).ConfigureAwait(false);
            var databaseHash = await HashFileAsync(databaseBackup, cancellationToken).ConfigureAwait(false);
            var manifest = new BackupManifest(
                1,
                applicationVersion,
                SqliteBucketRepository.CurrentSchemaVersion,
                DateTimeOffset.UtcNow,
                databaseHash);
            await File.WriteAllTextAsync(
                Path.Combine(temporaryDirectory, "manifest.json"),
                JsonSerializer.Serialize(manifest, ManifestJsonOptions),
                new UTF8Encoding(false),
                cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(settingsPath) && File.Exists(settingsPath))
            {
                File.Copy(settingsPath, Path.Combine(temporaryDirectory, "config.json"), true);
            }

            if (File.Exists(fullPath))
            {
                File.Delete(fullPath);
            }

            ZipFile.CreateFromDirectory(temporaryDirectory, fullPath, CompressionLevel.SmallestSize, false);
            log.Information(
                "backup_completed",
                $"size_bytes={new FileInfo(fullPath).Length} duration_ms={Stopwatch.GetElapsedTime(started).TotalMilliseconds:F1}");
            return fullPath;
        }
        finally
        {
            if (Directory.Exists(temporaryDirectory))
            {
                Directory.Delete(temporaryDirectory, true);
            }
        }
    }

    public async ValueTask ValidateBackupPackageAsync(
        string packagePath,
        CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(packagePath);
        using var archive = ZipFile.OpenRead(fullPath);
        var manifestEntry = archive.GetEntry("manifest.json") ?? throw new InvalidDataException("备份缺少 manifest.json。");
        var databaseEntry = archive.GetEntry("inputatlas.db") ?? throw new InvalidDataException("备份缺少 inputatlas.db。");
        BackupManifest manifest;
        await using (var manifestStream = manifestEntry.Open())
        {
            manifest = await JsonSerializer.DeserializeAsync<BackupManifest>(manifestStream, cancellationToken: cancellationToken)
                .ConfigureAwait(false) ?? throw new InvalidDataException("备份清单无效。");
        }

        if (manifest.FormatVersion != 1 || manifest.SchemaVersion > SqliteBucketRepository.CurrentSchemaVersion)
        {
            throw new NotSupportedException("备份格式或数据库版本高于当前应用支持范围。");
        }

        var temporaryDatabase = Path.Combine(Path.GetTempPath(), $"InputAtlasValidate-{Guid.NewGuid():N}.db");
        try
        {
            await using (var source = databaseEntry.Open())
            await using (var destination = new FileStream(temporaryDatabase, FileMode.CreateNew, FileAccess.Write, FileShare.None, 8192, true))
            {
                await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
            }

            var hash = await HashFileAsync(temporaryDatabase, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(hash, manifest.DatabaseSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("备份数据库哈希校验失败。");
            }

            var builder = new SqliteConnectionStringBuilder { DataSource = temporaryDatabase, Mode = SqliteOpenMode.ReadOnly };
            await using var connection = new SqliteConnection(builder.ToString());
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            var command = connection.CreateCommand();
            command.CommandText = "PRAGMA integrity_check;";
            if (!string.Equals((string?)await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), "ok", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("备份数据库完整性检查失败。");
            }

            log.Information("backup_validated", "备份格式、哈希与数据库完整性检查通过");
        }
        finally
        {
            if (File.Exists(temporaryDatabase))
            {
                File.Delete(temporaryDatabase);
            }
        }
    }

    public async ValueTask RestoreBackupPackageAsync(
        string packagePath,
        string? settingsPath,
        CancellationToken cancellationToken = default)
    {
        await ValidateBackupPackageAsync(packagePath, cancellationToken).ConfigureAwait(false);
        var started = Stopwatch.GetTimestamp();
        var dataDirectory = Path.GetDirectoryName(repository.DatabasePath)!;
        var incomingDatabase = Path.Combine(dataDirectory, $"inputatlas.restore-{Guid.NewGuid():N}.db");
        var rollbackDatabase = repository.DatabasePath + ".restore-rollback";
        var incomingSettings = settingsPath is null ? null : settingsPath + ".restore";

        try
        {
            using (var archive = ZipFile.OpenRead(Path.GetFullPath(packagePath)))
            {
                var databaseEntry = archive.GetEntry("inputatlas.db")!;
                await using (var source = databaseEntry.Open())
                await using (var destination = new FileStream(incomingDatabase, FileMode.CreateNew, FileAccess.Write, FileShare.None, 8192, true))
                {
                    await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
                    await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
                }

                if (incomingSettings is not null && archive.GetEntry("config.json") is { } settingsEntry)
                {
                    await using var source = settingsEntry.Open();
                    await using var destination = new FileStream(incomingSettings, FileMode.Create, FileAccess.Write, FileShare.None, 4096, true);
                    await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
                    await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
            }

            SqliteConnection.ClearAllPools();
            DeleteSidecarFiles(repository.DatabasePath);
            if (File.Exists(rollbackDatabase))
            {
                File.Delete(rollbackDatabase);
            }

            File.Move(repository.DatabasePath, rollbackDatabase);
            File.Move(incomingDatabase, repository.DatabasePath);
            if (!await repository.IntegrityCheckAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidDataException("恢复后的数据库完整性检查失败。");
            }

            if (incomingSettings is not null && File.Exists(incomingSettings))
            {
                File.Move(incomingSettings, settingsPath!, true);
            }

            File.Delete(rollbackDatabase);
            log.Information("restore_completed", $"duration_ms={Stopwatch.GetElapsedTime(started).TotalMilliseconds:F1}");
        }
        catch
        {
            SqliteConnection.ClearAllPools();
            DeleteSidecarFiles(repository.DatabasePath);
            if (File.Exists(rollbackDatabase))
            {
                File.Move(rollbackDatabase, repository.DatabasePath, true);
            }

            log.Warning("restore_rolled_back", "恢复失败，已回滚到原数据库");
            throw;
        }
        finally
        {
            if (File.Exists(incomingDatabase))
            {
                File.Delete(incomingDatabase);
            }

            if (incomingSettings is not null && File.Exists(incomingSettings))
            {
                File.Delete(incomingSettings);
            }
        }
    }

    private static async ValueTask<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, true);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash);
    }

    private static void DeleteSidecarFiles(string databasePath)
    {
        foreach (var suffix in new[] { "-wal", "-shm" })
        {
            var path = databasePath + suffix;
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    private static string EscapeCsv(string value) =>
        value.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? $"\"{value.Replace("\"", "\"\"")}\"" : value;

    private static string DisplayName(InputId input) => input.Value switch
    {
        900 => "其他键",
        1001 => "鼠标左键",
        1002 => "鼠标右键",
        1003 => "鼠标中键",
        1004 => "鼠标后侧键",
        1005 => "鼠标前侧键",
        1011 => "滚轮向上",
        1012 => "滚轮向下",
        1013 => "滚轮向左",
        1014 => "滚轮向右",
        _ => $"HID-{input.Value}",
    };

    private sealed record BackupManifest(
        int FormatVersion,
        string ApplicationVersion,
        int SchemaVersion,
        DateTimeOffset CreatedUtc,
        string DatabaseSha256);
}
