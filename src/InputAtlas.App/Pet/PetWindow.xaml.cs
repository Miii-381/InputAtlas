using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using InputAtlas.Core;
using Forms = System.Windows.Forms;

namespace InputAtlas.App.Pet;

public partial class PetWindow : Window
{
    private const int ExtendedStyleIndex = -20;
    private const long ToolWindowStyle = 0x00000080L;
    private const long TransparentStyle = 0x00000020L;
    private const long NoActivateStyle = 0x08000000L;
    private const uint SetPositionNoSize = 0x0001;
    private const uint SetPositionNoZOrder = 0x0004;
    private const uint SetPositionNoActivate = 0x0010;
    private const uint SetPositionNoOwnerZOrder = 0x0200;
    private const double BaseWidth = 420;
    private const double AspectRatio = 1403d / 1123d;
    private readonly PetFrameSet _frames;
    private readonly IApplicationLog _log;
    private readonly DoubleAnimationUsingKeyFrames _fullScalePulse = CreateScalePulse(1.03);
    private readonly DoubleAnimationUsingKeyFrames _reducedScalePulse = CreateScalePulse(1.012);
    private readonly DoubleAnimationUsingKeyFrames _fullTranslatePulse = CreateTranslatePulse(-7);
    private readonly DoubleAnimationUsingKeyFrames _reducedTranslatePulse = CreateTranslatePulse(-3);
    private bool _clickThrough;
    private bool _adjusting;
    private bool _dragging;
    private bool _dragMoveFailureLogged;
    private bool _snappedHorizontal;
    private bool _snappedVertical;
    private Point _dragRelativeAnchor;

    public PetWindow(PetFrameSet frames, IApplicationLog log)
    {
        _frames = frames;
        _log = log;
        InitializeComponent();
        SetFrame(PetVisualState.Idle);
        SourceInitialized += (_, _) => ApplyExtendedStyles();
        DpiChanged += (_, e) =>
        {
            if (_adjusting && _dragging)
            {
                _log.Information(
                    "pet_adjustment_dpi_changed",
                    $"old_scale_x={e.OldDpi.DpiScaleX:F2} old_scale_y={e.OldDpi.DpiScaleY:F2} new_scale_x={e.NewDpi.DpiScaleX:F2} new_scale_y={e.NewDpi.DpiScaleY:F2} anchor_preserved=true");
                Dispatcher.BeginInvoke(RefreshDragAfterDpiChanged);
            }
            else
            {
                PlacementRefreshRequested?.Invoke(this, EventArgs.Empty);
            }
        };
    }

    public event EventHandler? PlacementCommitted;

    public event EventHandler? PlacementRefreshRequested;

    public bool IsAdjusting => _adjusting;

    public void SetFrame(PetVisualState state)
    {
        FrameImage.Source = _frames[state];
    }

    public void PlayActionPulse(bool reducedMotion)
    {
        var scaleAnimation = reducedMotion ? _reducedScalePulse : _fullScalePulse;
        var translateAnimation = reducedMotion ? _reducedTranslatePulse : _fullTranslatePulse;
        PulseScale.BeginAnimation(
            System.Windows.Media.ScaleTransform.ScaleXProperty,
            scaleAnimation,
            HandoffBehavior.SnapshotAndReplace);
        PulseScale.BeginAnimation(
            System.Windows.Media.ScaleTransform.ScaleYProperty,
            scaleAnimation,
            HandoffBehavior.SnapshotAndReplace);
        PulseTranslate.BeginAnimation(
            System.Windows.Media.TranslateTransform.YProperty,
            translateAnimation,
            HandoffBehavior.SnapshotAndReplace);
    }

    public void ApplyPresentation(bool topmost, bool clickThrough, double scale)
    {
        Topmost = topmost;
        _clickThrough = clickThrough;
        Width = BaseWidth * scale;
        Height = Width / AspectRatio;
        ApplyExtendedStyles();
    }

    public void BeginAdjustment()
    {
        _adjusting = true;
        _dragging = false;
        _dragMoveFailureLogged = false;
        _snappedHorizontal = false;
        _snappedVertical = false;
        AdjustmentOutline.Visibility = Visibility.Visible;
        AdjustmentOutline.BorderBrush = Brushes.BlueViolet;
        Cursor = Cursors.SizeAll;
        Focusable = true;
        ApplyExtendedStyles();
        Activate();
        _log.Information("pet_adjustment_started", "桌宠已进入单次拖动调整模式");
    }

    private void EndAdjustment()
    {
        if (!_adjusting)
        {
            return;
        }

        _adjusting = false;
        _dragging = false;
        if (ReferenceEquals(Mouse.Captured, PetSurface))
        {
            Mouse.Capture(null);
        }

        AdjustmentOutline.Visibility = Visibility.Collapsed;
        Cursor = Cursors.Arrow;
        Focusable = false;
        ApplyExtendedStyles();
        PlacementCommitted?.Invoke(this, EventArgs.Empty);
        _log.Information(
            "pet_adjustment_completed",
            $"鼠标穿透设置已恢复 snapped_horizontal={_snappedHorizontal} snapped_vertical={_snappedVertical}");
    }

    private void PetSurfaceMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!_adjusting || e.ButtonState != MouseButtonState.Pressed)
        {
            return;
        }

        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero ||
            !GetCursorPos(out var cursor) ||
            !GetWindowRect(handle, out var windowBounds))
        {
            _log.Warning(
                "pet_adjustment_drag_start_failed",
                $"native_error={Marshal.GetLastWin32Error()}");
            return;
        }

        _dragRelativeAnchor = PetWindowPlacementService.CaptureRelativeAnchor(
            new Rect(
                windowBounds.Left,
                windowBounds.Top,
                windowBounds.Right - windowBounds.Left,
                windowBounds.Bottom - windowBounds.Top),
            new Point(cursor.X, cursor.Y));

        _dragging = Mouse.Capture(PetSurface, CaptureMode.Element);
        if (!_dragging)
        {
            _log.Warning("pet_adjustment_mouse_capture_failed", "无法捕获鼠标以开始桌宠位置调整");
            return;
        }

        e.Handled = true;
    }

    private void PetSurfaceMouseMove(object sender, MouseEventArgs e)
    {
        if (!_adjusting || !_dragging)
        {
            return;
        }

        if (e.LeftButton != MouseButtonState.Pressed)
        {
            EndAdjustment();
            return;
        }

        if (!GetCursorPos(out var cursor))
        {
            LogDragMoveFailureOnce("cursor_unavailable");
            return;
        }

        MoveWindowToCursor(cursor);
        e.Handled = true;
    }

    private void RefreshDragAfterDpiChanged()
    {
        if (!_adjusting || !_dragging)
        {
            return;
        }

        if (!GetCursorPos(out var cursor))
        {
            LogDragMoveFailureOnce("dpi_change_cursor_unavailable");
            return;
        }

        MoveWindowToCursor(cursor);
    }

    private void MoveWindowToCursor(NativePoint cursor)
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero || !GetWindowRect(handle, out var windowBounds))
        {
            LogDragMoveFailureOnce("window_bounds_unavailable");
            return;
        }

        var width = windowBounds.Right - windowBounds.Left;
        var height = windowBounds.Bottom - windowBounds.Top;
        if (width <= 0 || height <= 0)
        {
            LogDragMoveFailureOnce("window_bounds_invalid");
            return;
        }

        var requested = PetWindowPlacementService.PositionFromRelativeAnchor(
            new Point(cursor.X, cursor.Y),
            new Size(width, height),
            _dragRelativeAnchor);
        var requestedLeft = checked((int)Math.Round(requested.X));
        var requestedTop = checked((int)Math.Round(requested.Y));
        var screen = Forms.Screen.FromRectangle(new System.Drawing.Rectangle(
            requestedLeft,
            requestedTop,
            width,
            height));
        var workingArea = screen.WorkingArea;
        var dpi = GetDpiForWindow(handle);
        var dpiScale = dpi == 0 ? 1d : dpi / 96d;
        var result = PetWindowPlacementService.Snap(
            new Rect(workingArea.Left, workingArea.Top, workingArea.Width, workingArea.Height),
            new Size(width, height),
            new Point(requestedLeft, requestedTop),
            PetWindowPlacementService.DefaultSnapDistance * dpiScale);

        _snappedHorizontal = result.Horizontal;
        _snappedVertical = result.Vertical;
        AdjustmentOutline.BorderBrush = result.Horizontal || result.Vertical
            ? Brushes.MediumSeaGreen
            : Brushes.BlueViolet;
        if (!SetWindowPos(
                handle,
                IntPtr.Zero,
                checked((int)Math.Round(result.Position.X)),
                checked((int)Math.Round(result.Position.Y)),
                0,
                0,
                SetPositionNoSize | SetPositionNoZOrder | SetPositionNoActivate | SetPositionNoOwnerZOrder))
        {
            LogDragMoveFailureOnce("set_window_position_failed");
        }
    }

    private void PetSurfaceMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_adjusting || !_dragging)
        {
            return;
        }

        EndAdjustment();
        e.Handled = true;
    }

    private void PetSurfaceLostMouseCapture(object sender, MouseEventArgs e)
    {
        if (_adjusting && _dragging)
        {
            EndAdjustment();
        }
    }

    private void LogDragMoveFailureOnce(string reason)
    {
        if (_dragMoveFailureLogged)
        {
            return;
        }

        _dragMoveFailureLogged = true;
        _log.Warning(
            "pet_adjustment_drag_move_failed",
            $"reason={reason} native_error={Marshal.GetLastWin32Error()}");
    }

    private void ApplyExtendedStyles()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var styles = GetWindowLongPtr(handle, ExtendedStyleIndex).ToInt64();
        styles |= ToolWindowStyle;
        if (_adjusting)
        {
            styles &= ~NoActivateStyle;
        }
        else
        {
            styles |= NoActivateStyle;
        }

        if (_clickThrough && !_adjusting)
        {
            styles |= TransparentStyle;
        }
        else
        {
            styles &= ~TransparentStyle;
        }

        SetWindowLongPtr(handle, ExtendedStyleIndex, new IntPtr(styles));
    }

    private static DoubleAnimationUsingKeyFrames CreateScalePulse(double peak)
    {
        var animation = new DoubleAnimationUsingKeyFrames
        {
            FillBehavior = FillBehavior.Stop,
        };
        animation.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        animation.KeyFrames.Add(new EasingDoubleKeyFrame(
            peak,
            KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(45)),
            new QuadraticEase { EasingMode = EasingMode.EaseOut }));
        animation.KeyFrames.Add(new EasingDoubleKeyFrame(
            1,
            KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(125)),
            new QuadraticEase { EasingMode = EasingMode.EaseInOut }));
        animation.Freeze();
        return animation;
    }

    private static DoubleAnimationUsingKeyFrames CreateTranslatePulse(double peak)
    {
        var animation = new DoubleAnimationUsingKeyFrames
        {
            FillBehavior = FillBehavior.Stop,
        };
        animation.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        animation.KeyFrames.Add(new EasingDoubleKeyFrame(
            peak,
            KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(45)),
            new QuadraticEase { EasingMode = EasingMode.EaseOut }));
        animation.KeyFrames.Add(new EasingDoubleKeyFrame(
            0,
            KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(125)),
            new QuadraticEase { EasingMode = EasingMode.EaseInOut }));
        animation.Freeze();
        return animation;
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr newValue);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr window);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr window, out MovingRect rectangle);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr window,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MovingRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
