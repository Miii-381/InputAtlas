using System.Diagnostics;
using System.IO;
using InputAtlas.App;
using InputAtlas.Core;
using InputAtlas.Storage;
using InputAtlas.Windows;
using Microsoft.Data.Sqlite;

namespace InputAtlas.App.Tests;

public sealed class CapturePersistenceTests
{
    [Fact]
    public async Task SyncUsesMemoryUntilCheckpointAndSavesLastChangesAtShutdown()
    {
        var directory = Directory.CreateTempSubdirectory("InputAtlas-sync-");
        try
        {
            await using var log = new AsyncRollingLog(directory.FullName);
            await using var repository = new SqliteBucketRepository(Path.Combine(directory.FullName, "test.db"));
            await repository.InitializeAsync();
            const long start = 1_800_000_000;
            var clock = new TestClock(start);
            await using var capture = new RawInputCaptureController(log, clock);
            var firstSync = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var saved = BucketSnapshot.Create(start, 2, [new(InputId.WheelDown, 3)], start + 2);
            await repository.UpsertFiveMinuteAsync(saved);
            await using (var sync = new CapturePersistenceCoordinator(capture, repository, log))
            {
                await sync.ReloadCurrentBucketAsync();
                Assert.Equal(3, capture.GetCurrentSnapshot().Counts[InputId.WheelDown]);
                sync.SnapshotChanged += () => firstSync.TrySetResult();
                var started = Stopwatch.StartNew();
                sync.Start();
                capture.RestoreBucket(start, BucketSnapshot.Create(start, 3, [new(InputId.WheelDown, 7)], start + 3));
                Assert.Equal(7, Assert.Single(await sync.ReadRangeAsync(start, start + 300, default)).Counts[InputId.WheelDown]);
                Assert.Equal(3, Assert.Single(await repository.ReadRangeAsync(start, start + 300)).Counts[InputId.WheelDown]);
                Assert.False(firstSync.Task.IsCompleted);

                await firstSync.Task.WaitAsync(TimeSpan.FromSeconds(20));
                Assert.True(started.Elapsed >= TimeSpan.FromSeconds(9));
                Assert.Equal(7, Assert.Single(await repository.ReadRangeAsync(start, start + 300)).Counts[InputId.WheelDown]);
                capture.RestoreBucket(start, BucketSnapshot.Create(start, 4, [new(InputId.WheelDown, 9)], start + 4));
                await sync.SaveNowAsync();
                Assert.Equal(9, Assert.Single(await repository.ReadRangeAsync(start, start + 300)).Counts[InputId.WheelDown]);

                capture.RestoreBucket(start, BucketSnapshot.Create(start, 5, [new(InputId.WheelDown, 11)], start + 5));
            }

            Assert.Equal(11, Assert.Single(await repository.ReadRangeAsync(start, start + 300)).Counts[InputId.WheelDown]);
        }
        finally
        {
            directory.Delete(true);
        }
    }

    [Fact]
    public async Task FailedWriteCanBeRetriedAndDeletedBucketsStayDeleted()
    {
        var directory = Directory.CreateTempSubdirectory("InputAtlas-sync-retry-");
        try
        {
            await using var log = new AsyncRollingLog(directory.FullName);
            await using var repository = new SqliteBucketRepository(Path.Combine(directory.FullName, "test.db"));
            await repository.InitializeAsync();
            const long start = 1_800_000_000;
            await using var capture = new RawInputCaptureController(log, new TestClock(start));
            await using var sync = new CapturePersistenceCoordinator(capture, repository, log);
            capture.RestoreBucket(start, BucketSnapshot.Create(start, 35, [new(InputId.WheelUp, 7)], start + 35));
            await using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = repository.DatabasePath }.ToString());
            await db.OpenAsync();
            using var command = db.CreateCommand();
            command.CommandText = """
                CREATE TRIGGER reject_write BEFORE INSERT ON bucket_5m
                BEGIN SELECT RAISE(FAIL, 'test write failure'); END;
                """;
            await command.ExecuteNonQueryAsync();
            await Assert.ThrowsAsync<SqliteException>(() => sync.SaveNowAsync());
            Assert.Null(sync.LastSavedUtc);
            Assert.Equal(7, Assert.Single(await sync.ReadRangeAsync(start, start + 300, default)).Counts[InputId.WheelUp]);

            command.CommandText = "DROP TRIGGER reject_write";
            await command.ExecuteNonQueryAsync();
            await sync.SaveNowAsync();
            Assert.Equal(7, Assert.Single(await repository.ReadRangeAsync(start, start + 300)).Counts[InputId.WheelUp]);

            await sync.ChangeStoredDataAsync(() => repository.DeleteRangeAsync(start, start + 300).AsTask());
            await sync.SaveNowAsync();
            Assert.Empty(capture.GetCurrentSnapshot().Counts);
            Assert.Empty(await repository.ReadRangeAsync(start, start + 300));
        }
        finally
        {
            directory.Delete(true);
        }
    }

    private sealed class TestClock(long unixSeconds) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
    }
}
