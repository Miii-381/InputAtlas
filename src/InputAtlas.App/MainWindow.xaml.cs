using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using InputAtlas.Core;
using InputAtlas.Storage;

namespace InputAtlas.App;

public partial class MainWindow : Window
{
    private const uint MonitorDefaultToNearest = 2;
    private const int WmGetMinMaxInfo = 0x0024;
    private const double DockApproachHorizontalDistance = 96;
    private const double DockApproachVerticalDistance = 72;
    private const double DockDismissHorizontalDistance = 128;
    private const double DockDismissVerticalDistance = 88;
    private readonly MainViewModel _viewModel;
    private readonly IApplicationLog _log;
    private readonly bool _animationsEnabled;
    private readonly DispatcherTimer _navigationDockHideTimer;
    private HwndSource? _windowSource;
    private bool _allowClose;
    private bool _navigationDockCollapsed;
    private bool _navigationDockManuallyCollapsed;
    private bool _trayNoticeShown;

    public event EventHandler? HiddenToTray;

    public event EventHandler? PetVisibilityToggleRequested;

    public event EventHandler? PetAdjustmentRequested;

    public MainWindow(MainViewModel viewModel, AppSettings settings, IApplicationLog log)
    {
        _viewModel = viewModel;
        _log = log;
        _animationsEnabled = settings.Animation != AnimationKind.Off && SystemParameters.ClientAreaAnimation;
        InitializeComponent();
        _navigationDockHideTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(650),
        };
        _navigationDockHideTimer.Tick += NavigationDockHideTimerTick;
        DataContext = viewModel;
        Width = Math.Max(MinWidth, settings.WindowWidth);
        Height = Math.Max(MinHeight, settings.WindowHeight);
        SourceInitialized += (_, _) =>
        {
            _windowSource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
            _windowSource?.AddHook(WindowMessageHook);
            ClampToCurrentMonitorWorkArea();
            _log.Information("window_shell_initialized", $"caption_height=48 resize_border=6 state={WindowState}");
        };
        StateChanged += (_, _) =>
            _log.Information("window_shell_state_changed", $"state={WindowState}");
        DpiChanged += (_, _) => Dispatcher.BeginInvoke(ClampToCurrentMonitorWorkArea);
        if (settings.WindowMaximized)
        {
            WindowState = WindowState.Maximized;
        }

        _viewModel.InputVisualStateChanged += InputVisualStateChanged;
        Closed += (_, _) =>
        {
            _navigationDockHideTimer.Stop();
            _windowSource?.RemoveHook(WindowMessageHook);
            _viewModel.InputVisualStateChanged -= InputVisualStateChanged;
        };
        Loaded += async (_, _) =>
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            _viewModel.ReportDisplayScale(dpi.DpiScaleX, dpi.DpiScaleY);
            await _viewModel.InitializeAsync();
            UpdateThemeSwatchSelection();
            EvaluateNavigationDockPointer(System.Windows.Input.Mouse.GetPosition(RootLayout));
        };
    }

    public void AllowClose()
    {
        _allowClose = true;
        Close();
    }

    private void MinimizeWindowClick(object sender, RoutedEventArgs e)
    {
        _log.Information("window_shell_action", "action=minimize");
        SystemCommands.MinimizeWindow(this);
    }

    private void ToggleMaximizeWindowClick(object sender, RoutedEventArgs e)
    {
        var restore = WindowState == WindowState.Maximized;
        _log.Information("window_shell_action", $"action={(restore ? "restore" : "maximize")}");
        if (restore)
        {
            SystemCommands.RestoreWindow(this);
        }
        else
        {
            SystemCommands.MaximizeWindow(this);
        }
    }

    // 统一进入已有的 Closing 流程，继续保留关闭后隐藏到托盘的行为。
    private void CloseWindowClick(object sender, RoutedEventArgs e)
    {
        _log.Information("window_shell_action", "action=close_to_tray");
        SystemCommands.CloseWindow(this);
    }

    private void WindowClosing(object? sender, CancelEventArgs e)
    {
        if (_allowClose)
        {
            return;
        }

        e.Cancel = true;
        Hide();
        HiddenToTray?.Invoke(this, EventArgs.Empty);
        if (!_trayNoticeShown)
        {
            _trayNoticeShown = true;
            MessageBox.Show("InputAtlas 已隐藏到托盘并继续记录。可从托盘菜单真正退出。", "InputAtlas");
        }
    }

    private void KeyboardSelected(InputId input) => _viewModel.SelectInput(input);

    private void WindowPreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        var keyboardPoint = e.GetPosition(KeyboardMap);
        if (ReferenceEquals(e.OriginalSource, KeyboardMap) &&
            KeyboardMap.IsVisible &&
            KeyboardMap.HitTestInput(keyboardPoint) is not null)
        {
            return;
        }

        _viewModel.ClearSelectedInput();
    }

    private void InputVisualStateChanged(InputId input, bool isPressed)
    {
        if (!_animationsEnabled || !IsActive || !IsVisible || WindowState == WindowState.Minimized)
        {
            return;
        }

        KeyboardMap.SetInputState(input, isPressed);
        MouseMap.SetInputState(input, isPressed);
    }

    private void WindowDeactivated(object? sender, EventArgs e)
    {
        KeyboardMap.ResetAnimations();
        MouseMap.ResetAnimations();
        ScheduleNavigationDockHide();
    }

    private void NavigationDockMouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        _navigationDockHideTimer.Stop();
        if (_navigationDockManuallyCollapsed)
        {
            return;
        }

        SetNavigationDockCollapsed(false, ReferenceEquals(sender, NavigationDockReveal) ? "proximity_handle" : "pointer_enter");
    }

    private void NavigationDockMouseLeave(object sender, System.Windows.Input.MouseEventArgs e) =>
        EvaluateNavigationDockPointer(e.GetPosition(RootLayout));

    private void WindowPreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e) =>
        EvaluateNavigationDockPointer(e.GetPosition(RootLayout));

    private void NavigationDockCollapseClick(object sender, RoutedEventArgs e)
    {
        _navigationDockManuallyCollapsed = true;
        _navigationDockHideTimer.Stop();
        SetNavigationDockCollapsed(true, "manual_collapse");
        Debug.WriteLine("event=navigation_dock_manual_lock enabled=true");
        e.Handled = true;
    }

    private void NavigationDockRevealMouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _navigationDockManuallyCollapsed = false;
        _navigationDockHideTimer.Stop();
        SetNavigationDockCollapsed(false, "manual_reveal");
        Debug.WriteLine("event=navigation_dock_manual_lock enabled=false");
        e.Handled = true;
    }

    private void EvaluateNavigationDockPointer(Point pointer)
    {
        if (_navigationDockManuallyCollapsed || RootLayout.ActualWidth <= 0 || RootLayout.ActualHeight <= 0)
        {
            _navigationDockHideTimer.Stop();
            return;
        }

        var dockBounds = GetNavigationDockRestingBounds();
        if (_navigationDockCollapsed)
        {
            dockBounds.Inflate(DockApproachHorizontalDistance, DockApproachVerticalDistance);
            if (dockBounds.Contains(pointer))
            {
                _navigationDockHideTimer.Stop();
                SetNavigationDockCollapsed(false, "pointer_approach");
            }

            return;
        }

        dockBounds.Inflate(DockDismissHorizontalDistance, DockDismissVerticalDistance);
        if (dockBounds.Contains(pointer))
        {
            _navigationDockHideTimer.Stop();
            return;
        }

        ScheduleNavigationDockHide();
    }

    private Rect GetNavigationDockRestingBounds()
    {
        var width = Math.Max(NavigationDock.MinWidth, NavigationDock.ActualWidth);
        var height = Math.Max(NavigationDock.Height, NavigationDock.ActualHeight);
        var left = Math.Max(0, (RootLayout.ActualWidth - width) / 2);
        var bottom = Math.Max(height, RootLayout.ActualHeight - NavigationDock.Margin.Bottom);
        return new Rect(left, bottom - height, width, height);
    }

    private void ScheduleNavigationDockHide()
    {
        if (_navigationDockManuallyCollapsed || _navigationDockCollapsed || _navigationDockHideTimer.IsEnabled)
        {
            return;
        }

        _navigationDockHideTimer.Start();
    }

    private void NavigationDockHideTimerTick(object? sender, EventArgs e)
    {
        _navigationDockHideTimer.Stop();
        if (_navigationDockManuallyCollapsed || _navigationDockCollapsed)
        {
            return;
        }

        var dockBounds = GetNavigationDockRestingBounds();
        dockBounds.Inflate(DockDismissHorizontalDistance, DockDismissVerticalDistance);
        if (dockBounds.Contains(System.Windows.Input.Mouse.GetPosition(RootLayout)))
        {
            _navigationDockHideTimer.Stop();
            return;
        }

        SetNavigationDockCollapsed(true, "pointer_distant");
    }

    private void SetNavigationDockCollapsed(bool collapsed, string reason)
    {
        if (_navigationDockCollapsed == collapsed)
        {
            return;
        }

        _navigationDockCollapsed = collapsed;
        Debug.WriteLine($"event=navigation_dock_state collapsed={collapsed} reason={reason} manual_lock={_navigationDockManuallyCollapsed}");
        var targetOffset = collapsed ? 96d : 0d;
        var targetOpacity = collapsed ? 0d : 1d;
        NavigationDockReveal.IsHitTestVisible = collapsed;
        if (!_animationsEnabled)
        {
            NavigationDockTranslate.BeginAnimation(TranslateTransform.YProperty, null);
            NavigationDock.BeginAnimation(OpacityProperty, null);
            NavigationDockReveal.BeginAnimation(OpacityProperty, null);
            NavigationDockTranslate.Y = targetOffset;
            NavigationDock.Opacity = targetOpacity;
            NavigationDockReveal.Opacity = collapsed ? 1d : 0d;
            return;
        }

        var easing = new CubicEase
        {
            EasingMode = collapsed ? EasingMode.EaseIn : EasingMode.EaseOut,
        };
        var currentOffset = NavigationDockTranslate.Y;
        var currentDockOpacity = NavigationDock.Opacity;
        var currentRevealOpacity = NavigationDockReveal.Opacity;
        NavigationDockTranslate.BeginAnimation(TranslateTransform.YProperty, null);
        NavigationDock.BeginAnimation(OpacityProperty, null);
        NavigationDockReveal.BeginAnimation(OpacityProperty, null);
        // 先提交精确的最终基值，动画停止后自动回到整数 DIP / 完整不透明度的清晰渲染路径。
        NavigationDockTranslate.Y = targetOffset;
        NavigationDock.Opacity = targetOpacity;
        NavigationDockReveal.Opacity = collapsed ? 1d : 0d;
        NavigationDockTranslate.BeginAnimation(
            TranslateTransform.YProperty,
            new DoubleAnimation(currentOffset, targetOffset, TimeSpan.FromMilliseconds(collapsed ? 360 : 300))
            {
                EasingFunction = easing,
                FillBehavior = FillBehavior.Stop,
            },
            HandoffBehavior.SnapshotAndReplace);
        NavigationDock.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(currentDockOpacity, targetOpacity, TimeSpan.FromMilliseconds(collapsed ? 280 : 240))
            {
                FillBehavior = FillBehavior.Stop,
            },
            HandoffBehavior.SnapshotAndReplace);
        NavigationDockReveal.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(currentRevealOpacity, collapsed ? 1d : 0d, TimeSpan.FromMilliseconds(collapsed ? 260 : 180))
            {
                BeginTime = collapsed ? TimeSpan.FromMilliseconds(120) : TimeSpan.Zero,
                FillBehavior = FillBehavior.Stop,
            },
            HandoffBehavior.SnapshotAndReplace);
    }

    private async void AnsiLayoutClick(object sender, RoutedEventArgs e) =>
        await _viewModel.ChangeLayoutAsync(KeyboardLayoutKind.Ansi104);

    private async void CompactLayoutClick(object sender, RoutedEventArgs e) =>
        await _viewModel.ChangeLayoutAsync(KeyboardLayoutKind.Compact75);

    private async void ToggleCaptureClick(object sender, RoutedEventArgs e) =>
        await _viewModel.ToggleCaptureAsync();

    private async void SaveNowClick(object sender, RoutedEventArgs e) =>
        await _viewModel.SaveNowAsync();

    private async void ExportClick(object sender, RoutedEventArgs e) =>
        await _viewModel.ExportTodayAsync();

    private async void BackupClick(object sender, RoutedEventArgs e) =>
        await _viewModel.CreateBackupAsync();

    private async void RestoreClick(object sender, RoutedEventArgs e) =>
        await _viewModel.RestoreBackupAsync();

    private async void DeleteRangeClick(object sender, RoutedEventArgs e) =>
        await _viewModel.DeleteSelectedHistoryAsync();

    private async void AutostartClick(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.CheckBox checkBox)
        {
            await _viewModel.SetStartWithWindowsAsync(checkBox.IsChecked == true);
        }
    }

    private async void PetEnabledClick(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.CheckBox checkBox)
        {
            await _viewModel.SetPetEnabledAsync(checkBox.IsChecked == true);
        }
    }

    private async void PetTopmostClick(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.CheckBox checkBox)
        {
            await _viewModel.SetPetTopmostAsync(checkBox.IsChecked == true);
        }
    }

    private async void PetClickThroughClick(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.CheckBox checkBox)
        {
            await _viewModel.SetPetClickThroughAsync(checkBox.IsChecked == true);
        }
    }

    private async void PetScaleChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (sender is System.Windows.Controls.ComboBox { SelectedValue: double scale })
        {
            await _viewModel.SetPetScaleAsync(scale);
        }
    }

    private void TogglePetVisibilityClick(object sender, RoutedEventArgs e) =>
        PetVisibilityToggleRequested?.Invoke(this, EventArgs.Empty);

    private void AdjustPetPositionClick(object sender, RoutedEventArgs e) =>
        PetAdjustmentRequested?.Invoke(this, EventArgs.Empty);

    private async void ResetPetPositionClick(object sender, RoutedEventArgs e) =>
        await _viewModel.ResetPetPlacementAsync();

    private async void ApplyHeatmapThresholdsClick(object sender, RoutedEventArgs e) =>
        await _viewModel.ApplyHeatmapThresholdsAsync();

    private async void HeatmapThresholdModeChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (sender is System.Windows.Controls.ComboBox { SelectedValue: HeatmapThresholdMode mode })
        {
            await _viewModel.ApplyHeatmapThresholdModeAsync(mode);
        }
    }

    private async void FontFamilyChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (sender is System.Windows.Controls.ComboBox { SelectedValue: string fontFamily })
        {
            await _viewModel.ApplyFontFamilyAsync(fontFamily);
        }
    }

    private async void ApplyThemeColorClick(object sender, RoutedEventArgs e)
    {
        if (await _viewModel.ApplyThemeColorAsync())
        {
            UpdateThemeSwatchSelection();
        }
    }

    private async void ThemeColorSwatchClick(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { Tag: string color } &&
            await _viewModel.ApplyThemeColorAsync(color))
        {
            UpdateThemeSwatchSelection();
        }
    }

    private void UpdateThemeSwatchSelection()
    {
        foreach (var button in ThemeSwatches.Children.OfType<System.Windows.Controls.Button>())
        {
            var selected = string.Equals(
                button.Tag as string,
                _viewModel.ThemeColorHexText,
                StringComparison.OrdinalIgnoreCase);
            button.Content = selected ? "✓" : null;
            button.Foreground = button.Background is SolidColorBrush swatchBrush
                ? new SolidColorBrush(ThemeColorService.GetContrastingForeground(swatchBrush.Color))
                : Brushes.White;
            button.FontWeight = FontWeights.Bold;
            button.BorderBrush = selected ? Brushes.Black : Brushes.White;
            button.BorderThickness = selected ? new Thickness(3) : new Thickness(2);
        }
    }

    private void ClampToCurrentMonitorWorkArea()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var monitor = MonitorFromWindow(handle, MonitorDefaultToNearest);
        var monitorInfo = new MonitorInfo
        {
            Size = Marshal.SizeOf<MonitorInfo>(),
        };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref monitorInfo))
        {
            return;
        }

        var dpi = VisualTreeHelper.GetDpi(this);
        var workWidth = (monitorInfo.WorkArea.Right - monitorInfo.WorkArea.Left) / dpi.DpiScaleX;
        var workHeight = (monitorInfo.WorkArea.Bottom - monitorInfo.WorkArea.Top) / dpi.DpiScaleY;
        var maximumWidth = Math.Max(MinWidth, workWidth - 24);
        var maximumHeight = Math.Max(MinHeight, workHeight - 24);
        Width = Math.Clamp(Width, MinWidth, maximumWidth);
        Height = Math.Clamp(Height, MinHeight, maximumHeight);
        _viewModel.ReportWindowMetrics(dpi.DpiScaleX, dpi.DpiScaleY, Width, Height, workWidth, workHeight);
    }

    private IntPtr WindowMessageHook(IntPtr handle, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message != WmGetMinMaxInfo)
        {
            return IntPtr.Zero;
        }

        var monitor = MonitorFromWindow(handle, MonitorDefaultToNearest);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info))
        {
            _log.Warning("window_maximize_bounds_unavailable", $"win32_error={Marshal.GetLastWin32Error()}");
            return IntPtr.Zero;
        }

        // WM_GETMINMAXINFO 使用物理像素，位置相对于目标显示器原点。
        // 直接使用该显示器的工作区，兼容任务栏位于任意边缘及负坐标副屏。
        var bounds = Marshal.PtrToStructure<MinMaxInfo>(lParam);
        bounds.MaxPosition.X = info.WorkArea.Left - info.MonitorArea.Left;
        bounds.MaxPosition.Y = info.WorkArea.Top - info.MonitorArea.Top;
        bounds.MaxSize.X = info.WorkArea.Right - info.WorkArea.Left;
        bounds.MaxSize.Y = info.WorkArea.Bottom - info.WorkArea.Top;
        // 本消息由我们接管，显式保留 WPF 最小尺寸约束；小工作区优先完整显示窗口。
        var dpi = VisualTreeHelper.GetDpi(this);
        bounds.MinTrackSize.X = Math.Min((int)Math.Ceiling(MinWidth * dpi.DpiScaleX), bounds.MaxSize.X);
        bounds.MinTrackSize.Y = Math.Min((int)Math.Ceiling(MinHeight * dpi.DpiScaleY), bounds.MaxSize.Y);
        Marshal.StructureToPtr(bounds, lParam, false);
        handled = true;
        _log.Debug("window_maximize_bounds_applied",
            $"monitor=({info.MonitorArea.Left},{info.MonitorArea.Top},{info.MonitorArea.Right},{info.MonitorArea.Bottom}) " +
            $"work=({info.WorkArea.Left},{info.WorkArea.Top},{info.WorkArea.Right},{info.WorkArea.Bottom}) " +
            $"max_position=({bounds.MaxPosition.X},{bounds.MaxPosition.Y}) max_size=({bounds.MaxSize.X},{bounds.MaxSize.Y})");
        return IntPtr.Zero;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr windowHandle, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitorHandle, ref MonitorInfo monitorInfo);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public NativePoint Reserved;
        public NativePoint MaxSize;
        public NativePoint MaxPosition;
        public NativePoint MinTrackSize;
        public NativePoint MaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect MonitorArea;
        public NativeRect WorkArea;
        public uint Flags;
    }
}
