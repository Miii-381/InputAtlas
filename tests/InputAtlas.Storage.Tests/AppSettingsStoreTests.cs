using InputAtlas.Core;
using InputAtlas.Storage;

namespace InputAtlas.Storage.Tests;

public sealed class AppSettingsStoreTests
{
    [Fact]
    public async Task CustomHeatmapThresholdsAndAccentColorRoundTrip()
    {
        var directory = Path.Combine(Path.GetTempPath(), "InputAtlas.Tests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "config.json");
        try
        {
            var store = new AppSettingsStore(path);
            await store.SaveAsync(new AppSettings
            {
                HeatmapCoolThreshold = 1200,
                HeatmapWarmThreshold = 4800,
                HeatmapHotThreshold = 12000,
                HeatmapThresholdMode = HeatmapThresholdMode.Percentile,
                AccentColor = "#00796B",
                FontFamily = "等线",
            });

            var loaded = await store.LoadAsync();

            Assert.Equal(1200, loaded.HeatmapCoolThreshold);
            Assert.Equal(4800, loaded.HeatmapWarmThreshold);
            Assert.Equal(12000, loaded.HeatmapHotThreshold);
            Assert.Equal("#00796B", loaded.AccentColor);
            Assert.Equal(HeatmapThresholdMode.Percentile, loaded.HeatmapThresholdMode);
            Assert.Equal("等线", loaded.FontFamily);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
    }

    [Fact]
    public async Task InvalidStoredHeatmapThresholdsFallBackToDefaults()
    {
        var directory = Path.Combine(Path.GetTempPath(), "InputAtlas.Tests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "config.json");
        try
        {
            var store = new AppSettingsStore(path);
            await store.SaveAsync(new AppSettings
            {
                HeatmapCoolThreshold = 5000,
                HeatmapWarmThreshold = 1000,
                HeatmapHotThreshold = 200,
            });

            var loaded = await store.LoadAsync();

            Assert.Equal(100, loaded.HeatmapCoolThreshold);
            Assert.Equal(500, loaded.HeatmapWarmThreshold);
            Assert.Equal(2000, loaded.HeatmapHotThreshold);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
    }

    [Fact]
    public async Task VersionOneDefaultThresholdsMigrateToNewSingleInputDefaults()
    {
        var directory = Path.Combine(Path.GetTempPath(), "InputAtlas.Tests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "config.json");
        try
        {
            var store = new AppSettingsStore(path);
            await store.SaveAsync(new AppSettings
            {
                SchemaVersion = 1,
                HeatmapCoolThreshold = 500,
                HeatmapWarmThreshold = 2500,
                HeatmapHotThreshold = 6000,
            });

            var loaded = await store.LoadAsync();

            Assert.Equal(5, loaded.SchemaVersion);
            Assert.Equal(100, loaded.HeatmapCoolThreshold);
            Assert.Equal(500, loaded.HeatmapWarmThreshold);
            Assert.Equal(2000, loaded.HeatmapHotThreshold);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
    }

    [Fact]
    public async Task PreviousDefaultAccentMigratesToWarmPalettePrimary()
    {
        var directory = Path.Combine(Path.GetTempPath(), "InputAtlas.Tests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "config.json");
        try
        {
            var store = new AppSettingsStore(path);
            await store.SaveAsync(new AppSettings
            {
                SchemaVersion = 2,
                AccentColor = "#635BFF",
            });

            var loaded = await store.LoadAsync();

            Assert.Equal(5, loaded.SchemaVersion);
            Assert.Equal("#F3D48D", loaded.AccentColor);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
    }

    [Fact]
    public async Task PreviousSaturatedAccentMigratesToUpdatedWarmPalettePrimary()
    {
        var directory = Path.Combine(Path.GetTempPath(), "InputAtlas.Tests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "config.json");
        try
        {
            var store = new AppSettingsStore(path);
            await store.SaveAsync(new AppSettings
            {
                SchemaVersion = 3,
                AccentColor = "#7FA2A2",
            });

            var loaded = await store.LoadAsync();

            Assert.Equal(5, loaded.SchemaVersion);
            Assert.Equal("#F3D48D", loaded.AccentColor);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
    }

    [Fact]
    public async Task PreviousDefaultTealMigratesButCustomAccentIsPreserved()
    {
        var directory = Path.Combine(Path.GetTempPath(), "InputAtlas.Tests", Guid.NewGuid().ToString("N"));
        var defaultPath = Path.Combine(directory, "default.json");
        var customPath = Path.Combine(directory, "custom.json");
        try
        {
            var defaultStore = new AppSettingsStore(defaultPath);
            await defaultStore.SaveAsync(new AppSettings
            {
                SchemaVersion = 4,
                AccentColor = "#6F9F9D",
            });
            var customStore = new AppSettingsStore(customPath);
            await customStore.SaveAsync(new AppSettings
            {
                SchemaVersion = 4,
                AccentColor = "#86A9CF",
            });

            var migratedDefault = await defaultStore.LoadAsync();
            var preservedCustom = await customStore.LoadAsync();

            Assert.Equal(5, migratedDefault.SchemaVersion);
            Assert.Equal("#F3D48D", migratedDefault.AccentColor);
            Assert.Equal(5, preservedCustom.SchemaVersion);
            Assert.Equal("#86A9CF", preservedCustom.AccentColor);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
    }
}
