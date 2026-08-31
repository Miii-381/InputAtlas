using System.Text.Json;
using System.Text.Json.Serialization;
using System.Diagnostics;
using InputAtlas.Core;

namespace InputAtlas.Storage;

public enum KeyboardLayoutKind
{
    Ansi104,
    Compact75,
}

public enum ThemeKind
{
    System,
    Light,
    Dark,
}

public enum AnimationKind
{
    Full,
    Reduced,
    Off,
}

public sealed record AppSettings
{
    public int SchemaVersion { get; init; } = 5;

    public bool OnboardingCompleted { get; init; }

    public KeyboardLayoutKind KeyboardLayout { get; init; } = KeyboardLayoutKind.Ansi104;

    public ThemeKind Theme { get; init; } = ThemeKind.System;

    public string HeatmapPalette { get; init; } = "ColorVisionSafe";

    public long HeatmapCoolThreshold { get; init; } = 100;

    public long HeatmapWarmThreshold { get; init; } = 500;

    public long HeatmapHotThreshold { get; init; } = 2000;

    public HeatmapThresholdMode HeatmapThresholdMode { get; init; } = HeatmapThresholdMode.FixedCount;

    public string AccentColor { get; init; } = "#F3D48D";

    public string FontFamily { get; init; } = "Segoe UI Variable Text";

    public AnimationKind Animation { get; init; } = AnimationKind.Full;

    public bool StartWithWindows { get; init; }

    public bool KeepFiveMinuteForever { get; init; }

    public DateTimeOffset? DiagnosticUntilUtc { get; init; }

    public double WindowWidth { get; init; } = 1280;

    public double WindowHeight { get; init; } = 800;

    public bool WindowMaximized { get; init; }
}

public sealed class AppSettingsStore(string settingsPath)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _settingsPath = Path.GetFullPath(settingsPath);

    public async ValueTask<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_settingsPath))
        {
            return new AppSettings();
        }

        await using var stream = new FileStream(
            _settingsPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var settings = await JsonSerializer.DeserializeAsync<AppSettings>(stream, JsonOptions, cancellationToken)
            .ConfigureAwait(false) ?? new AppSettings();
        var sourceSchemaVersion = settings.SchemaVersion;
        if (sourceSchemaVersion < 2 &&
            settings.HeatmapCoolThreshold == 500 &&
            settings.HeatmapWarmThreshold == 2500 &&
            settings.HeatmapHotThreshold == 6000)
        {
            Debug.WriteLine("event=settings_threshold_defaults_migrated schema_from=1 schema_to=2 cool=100 warm=500 hot=2000");
            settings = settings with
            {
                HeatmapCoolThreshold = 100,
                HeatmapWarmThreshold = 500,
                HeatmapHotThreshold = 2000,
            };
        }
        if (sourceSchemaVersion < 3 &&
            string.Equals(settings.AccentColor, "#635BFF", StringComparison.OrdinalIgnoreCase))
        {
            Debug.WriteLine("event=settings_accent_default_migrated schema_to=4 accent=#6F9F9D");
            settings = settings with { AccentColor = "#6F9F9D" };
        }
        if (sourceSchemaVersion < 4 &&
            string.Equals(settings.AccentColor, "#7FA2A2", StringComparison.OrdinalIgnoreCase))
        {
            Debug.WriteLine("event=settings_accent_saturation_migrated schema_to=4 accent=#6F9F9D");
            settings = settings with { AccentColor = "#6F9F9D" };
        }
        if (sourceSchemaVersion < 5 &&
            string.Equals(settings.AccentColor, "#6F9F9D", StringComparison.OrdinalIgnoreCase))
        {
            Debug.WriteLine("event=settings_accent_default_migrated schema_to=5 accent=#F3D48D");
            settings = settings with { AccentColor = "#F3D48D" };
        }

        settings = settings with
        {
            SchemaVersion = 5,
            FontFamily = string.IsNullOrWhiteSpace(settings.FontFamily)
                ? "Segoe UI Variable Text"
                : settings.FontFamily.Trim(),
        };

        if (HasValidHeatmapThresholds(settings))
        {
            return settings;
        }

        Debug.WriteLine($"event=settings_thresholds_invalid cool={settings.HeatmapCoolThreshold} warm={settings.HeatmapWarmThreshold} hot={settings.HeatmapHotThreshold} fallback=default");
        return settings with
        {
            HeatmapCoolThreshold = 100,
            HeatmapWarmThreshold = 500,
            HeatmapHotThreshold = 2000,
        };
    }

    public async ValueTask SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
        var temporary = _settingsPath + ".tmp";
        await using (var stream = new FileStream(
            temporary,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            4096,
            FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            await JsonSerializer.SerializeAsync(stream, settings, JsonOptions, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        File.Move(temporary, _settingsPath, true);
    }

    private static bool HasValidHeatmapThresholds(AppSettings settings) =>
        settings.HeatmapCoolThreshold > 0 &&
        settings.HeatmapCoolThreshold < settings.HeatmapWarmThreshold &&
        settings.HeatmapWarmThreshold < settings.HeatmapHotThreshold;
}
