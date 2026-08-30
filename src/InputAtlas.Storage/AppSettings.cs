using System.Text.Json;
using System.Text.Json.Serialization;

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
    public int SchemaVersion { get; init; } = 1;

    public bool OnboardingCompleted { get; init; }

    public KeyboardLayoutKind KeyboardLayout { get; init; } = KeyboardLayoutKind.Ansi104;

    public ThemeKind Theme { get; init; } = ThemeKind.System;

    public string HeatmapPalette { get; init; } = "ColorVisionSafe";

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
        return await JsonSerializer.DeserializeAsync<AppSettings>(stream, JsonOptions, cancellationToken)
            .ConfigureAwait(false) ?? new AppSettings();
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
}

