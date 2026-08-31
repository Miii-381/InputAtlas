using System.Windows;
using System.Windows.Media;

namespace InputAtlas.App;

public sealed record FontFamilyOption(string Value, string DisplayName, string Description)
{
    public override string ToString() => DisplayName;
}

public static class FontFamilyService
{
    public const string DefaultFontFamily = "Segoe UI Variable Text";

    public static IReadOnlyList<FontFamilyOption> Options { get; } =
    [
        new(DefaultFontFamily, "Segoe UI Variable Text", "Windows 11 推荐，字重和数字比例更现代"),
        new("Segoe UI", "Segoe UI", "Windows 原生无衬线，清晰耐读"),
        new("等线", "等线 DengXian", "中文界面更轻盈，适合长时间阅读"),
        new("Microsoft YaHei UI", "微软雅黑 UI", "兼容性最好，适合旧版 Windows"),
    ];

    public static string Normalize(string? value)
    {
        if (!string.IsNullOrWhiteSpace(value) &&
            Options.Any(option => string.Equals(option.Value, value.Trim(), StringComparison.OrdinalIgnoreCase)))
        {
            return Options.First(option => string.Equals(option.Value, value.Trim(), StringComparison.OrdinalIgnoreCase)).Value;
        }

        return DefaultFontFamily;
    }

    public static FontFamily Apply(string? requestedFontFamily)
    {
        var normalized = Normalize(requestedFontFamily);
        var composite = new FontFamily($"{normalized}, Segoe UI, Microsoft YaHei UI");
        if (Application.Current is { } application)
        {
            application.Resources["AppFontFamily"] = composite;
            foreach (Window window in application.Windows)
            {
                // 已打开窗口可能已经解析过样式资源，直接更新继承属性以立即刷新全部子控件。
                window.FontFamily = composite;
                window.InvalidateMeasure();
                window.InvalidateVisual();
            }
        }

        return composite;
    }
}
