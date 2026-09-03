using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using InputAtlas.Storage;
using Forms = System.Windows.Forms;

namespace InputAtlas.App.Pet;

public sealed record PetWindowPlacement(double Left, double Top, string? MonitorDeviceName);

public static class PetWindowPlacementService
{
    private const double VisibleMargin = 24;

    public const double DefaultSnapDistance = 18;

    public static void PlaceAndClamp(PetWindow window, AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(settings);

        var screen = FindScreen(settings.PetMonitorDeviceName);
        var dpi = VisualTreeHelper.GetDpi(window);
        var workArea = ToDips(screen.WorkingArea, dpi);
        var requested = settings.PetLeft is { } left && settings.PetTop is { } top
            ? new Point(left, top)
            : GetDefaultPosition(workArea, new Size(window.Width, window.Height));
        var clamped = Clamp(workArea, new Size(window.Width, window.Height), requested);
        window.Left = clamped.X;
        window.Top = clamped.Y;
    }

    public static Point GetDefaultPosition(Rect workArea, Size windowSize) =>
        new(
            workArea.Right - windowSize.Width - VisibleMargin,
            workArea.Bottom - windowSize.Height - VisibleMargin);

    public static PetWindowPlacement Capture(PetWindow window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        var screen = handle == IntPtr.Zero ? Forms.Screen.PrimaryScreen : Forms.Screen.FromHandle(handle);
        return new PetWindowPlacement(window.Left, window.Top, screen?.DeviceName);
    }

    public static (Point Position, bool Horizontal, bool Vertical) Snap(
        Rect workArea,
        Size windowSize,
        Point requested,
        double snapDistance = DefaultSnapDistance)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(snapDistance);
        var left = SnapAxis(
            requested.X,
            workArea.Left,
            workArea.Right - windowSize.Width,
            snapDistance,
            out var horizontal);
        var top = SnapAxis(
            requested.Y,
            workArea.Top,
            workArea.Bottom - windowSize.Height,
            snapDistance,
            out var vertical);
        return (new Point(left, top), horizontal, vertical);
    }

    public static Point Clamp(Rect workArea, Size windowSize, Point requested)
    {
        var minimumLeft = windowSize.Width <= workArea.Width
            ? workArea.Left
            : workArea.Right - windowSize.Width - VisibleMargin;
        var minimumTop = windowSize.Height <= workArea.Height
            ? workArea.Top
            : workArea.Bottom - windowSize.Height - VisibleMargin;
        var maximumLeft = windowSize.Width <= workArea.Width
            ? workArea.Right - windowSize.Width
            : workArea.Left + VisibleMargin;
        var maximumTop = windowSize.Height <= workArea.Height
            ? workArea.Bottom - windowSize.Height
            : workArea.Top + VisibleMargin;
        return new Point(
            Math.Clamp(requested.X, minimumLeft, maximumLeft),
            Math.Clamp(requested.Y, minimumTop, maximumTop));
    }

    private static Forms.Screen FindScreen(string? deviceName) =>
        Forms.Screen.AllScreens.FirstOrDefault(screen =>
            string.Equals(screen.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase)) ??
        Forms.Screen.PrimaryScreen ??
        Forms.Screen.AllScreens[0];

    private static double SnapAxis(
        double value,
        double leadingEdge,
        double trailingEdge,
        double snapDistance,
        out bool snapped)
    {
        var leadingDistance = Math.Abs(value - leadingEdge);
        var trailingDistance = Math.Abs(value - trailingEdge);
        var nearestEdge = leadingDistance <= trailingDistance ? leadingEdge : trailingEdge;
        var nearestDistance = Math.Min(leadingDistance, trailingDistance);
        snapped = nearestDistance <= snapDistance;
        return snapped ? nearestEdge : value;
    }

    private static Rect ToDips(System.Drawing.Rectangle rectangle, DpiScale dpi) =>
        new(
            rectangle.Left / dpi.DpiScaleX,
            rectangle.Top / dpi.DpiScaleY,
            rectangle.Width / dpi.DpiScaleX,
            rectangle.Height / dpi.DpiScaleY);
}
