using System.Reflection;
using System.Text.Json;
using InputAtlas.Core;
using InputAtlas.Storage;

namespace InputAtlas.App;

public static class KeyboardLayoutLoader
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static KeyboardLayoutDefinition Load(KeyboardLayoutKind kind)
    {
        var fileName = kind == KeyboardLayoutKind.Ansi104 ? "ansi104.json" : "compact75.json";
        var resourceName = $"InputAtlas.App.Layouts.{fileName}";
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"找不到嵌入布局资源 {resourceName}。");
        var dto = JsonSerializer.Deserialize<LayoutDto>(stream, Options)
            ?? throw new InvalidDataException("键盘布局 JSON 为空。");
        var keys = dto.Keys.Select(key => new KeyDefinition(
            new InputId(key.Input),
            key.Label,
            key.SecondaryLabel,
            key.X,
            key.Y,
            key.Width,
            key.Height,
            key.Observable,
            key.Group ?? "main")).ToArray();
        return new KeyboardLayoutDefinition(dto.Id, dto.DisplayName, dto.WidthUnits, dto.HeightUnits, keys);
    }

    private sealed record LayoutDto(
        string Id,
        string DisplayName,
        double WidthUnits,
        double HeightUnits,
        KeyDto[] Keys);

    private sealed record KeyDto(
        ushort Input,
        string Label,
        string? SecondaryLabel,
        double X,
        double Y,
        double Width,
        double Height,
        bool Observable = true,
        string? Group = null);
}

