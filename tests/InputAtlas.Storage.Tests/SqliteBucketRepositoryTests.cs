using InputAtlas.Core;
using InputAtlas.Storage;

namespace InputAtlas.Storage.Tests;

public sealed class SqliteBucketRepositoryTests : IAsyncLifetime, IAsyncDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "InputAtlasTests", Guid.NewGuid().ToString("N"));
    private SqliteBucketRepository _repository = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_directory);
        _repository = new SqliteBucketRepository(Path.Combine(_directory, "inputatlas.db"));
        await _repository.InitializeAsync();
    }

    public async Task DisposeAsync()
    {
        await _repository.DisposeAsync();
        Directory.Delete(_directory, true);
    }

    ValueTask IAsyncDisposable.DisposeAsync() => new(DisposeAsync());

    [Fact]
    public async Task UpsertIsIdempotentAndReplacesFullSnapshot()
    {
        var start = TimeBuckets.AlignFiveMinutes(1_800_000_000);
        await _repository.UpsertFiveMinuteAsync(BucketSnapshot.Create(
            start,
            30,
            [new(new InputId(4), 1)],
            start + 30));
        await _repository.UpsertFiveMinuteAsync(BucketSnapshot.Create(
            start,
            60,
            [new(new InputId(4), 2)],
            start + 60));

        var result = await _repository.ReadRangeAsync(start, start + 300);
        var bucket = Assert.Single(result);
        Assert.Equal(60, bucket.CoverageSeconds);
        Assert.Equal(2, bucket.Counts[new InputId(4)]);
    }

    [Fact]
    public async Task DatabaseUsesWalAndPassesIntegrityCheck()
    {
        Assert.True(await _repository.IntegrityCheckAsync());
        Assert.Equal("1", await _repository.GetMetadataAsync("schema_version"));
    }
}
