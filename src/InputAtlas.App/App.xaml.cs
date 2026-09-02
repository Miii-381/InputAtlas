using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using InputAtlas.Core;
using InputAtlas.Storage;
using InputAtlas.Windows;
using Forms = System.Windows.Forms;

namespace InputAtlas.App;

public partial class App : Application, IAsyncDisposable
{
    private SingleInstanceCoordinator? _singleInstance;
    private AsyncRollingLog? _log;
    private SqliteBucketRepository? _repository;
    private RawInputCaptureController? _capture;
    private CapturePersistenceCoordinator? _coordinator;
    private AppSettingsStore? _settingsStore;
    private AppSettings? _settings;
    private MainViewModel? _viewModel;
    private MainWindow? _window;
    private Forms.NotifyIcon? _tray;
    private System.Drawing.Icon? _trayIcon;
    private bool _shutdownStarted;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            var legacyStartupArgument = e.Args.Contains("--startup", StringComparer.OrdinalIgnoreCase);
            var startupCommand = e.Args.Contains("--shutdown-for-update", StringComparer.OrdinalIgnoreCase)
                ? "shutdown-for-update"
                : "activate";
            _singleInstance = new SingleInstanceCoordinator();
            if (!_singleInstance.TryAcquire())
            {
                var sent = await _singleInstance.SendCommandAsync(startupCommand, TimeSpan.FromSeconds(3));
                Debug.WriteLine($"InputAtlas 单实例命令已发送：command={startupCommand} sent={sent}");
                Shutdown();
                return;
            }

            if (string.Equals(startupCommand, "shutdown-for-update", StringComparison.Ordinal))
            {
                Debug.WriteLine("InputAtlas 未发现运行中的主实例，无需执行升级前关闭。");
                await _singleInstance.DisposeAsync();
                _singleInstance = null;
                Shutdown();
                return;
            }

            var dataRoot = Path.Combine(AppContext.BaseDirectory, "Data");
            var logRoot = Path.Combine(dataRoot, "Logs");
            _log = new AsyncRollingLog(logRoot);
            _log.Information(
                "application_start",
                $"version={GetVersion()} architecture={RuntimeInformation.ProcessArchitecture} runtime={Environment.Version} per_monitor_v2={Program.PerMonitorV2Enabled} legacy_startup_argument={legacyStartupArgument}");
            StartSingleInstanceCommandServer();

            _settingsStore = new AppSettingsStore(Path.Combine(dataRoot, "config.json"));
            _settings = await _settingsStore.LoadAsync();
            if (!ThemeColorService.TryNormalize(_settings.AccentColor, out var normalizedAccent))
            {
                normalizedAccent = ThemeColorService.DefaultAccentColor;
                _settings = _settings with { AccentColor = normalizedAccent };
                await _settingsStore.SaveAsync(_settings);
                _log.Information("settings_accent_color_repaired", $"accent={normalizedAccent}");
            }

            ThemeColorService.Apply(normalizedAccent);
            var normalizedFontFamily = FontFamilyService.Normalize(_settings.FontFamily);
            if (!string.Equals(normalizedFontFamily, _settings.FontFamily, StringComparison.Ordinal))
            {
                _settings = _settings with { FontFamily = normalizedFontFamily };
                await _settingsStore.SaveAsync(_settings);
                _log.Information("settings_font_family_repaired", $"font_family={normalizedFontFamily}");
            }

            FontFamilyService.Apply(normalizedFontFamily);
            _repository = new SqliteBucketRepository(Path.Combine(dataRoot, "inputatlas.db"));
            await _repository.InitializeAsync();

            if (!_settings.OnboardingCompleted)
            {
                var onboarding = new OnboardingWindow(_settings) { Owner = null };
                if (onboarding.ShowDialog() != true)
                {
                    await ShutdownApplicationAsync();
                    return;
                }

                _settings = onboarding.Result with { OnboardingCompleted = true };
                await _settingsStore.SaveAsync(_settings);
            }

            _capture = new RawInputCaptureController(_log);
            await _capture.StartAsync();
            StartupRegistration.SetEnabled(_settings.StartWithWindows, Environment.ProcessPath!);
            var autostartRegistered = StartupRegistration.IsEnabled();
            _log.Information(
                "autostart_registration_synchronized",
                $"configured={_settings.StartWithWindows} registry_enabled={autostartRegistered} launch_mode=foreground");
            if (await _repository.GetMetadataAsync("first_capture_utc") is null)
            {
                await _repository.SetMetadataAsync(
                    "first_capture_utc",
                    DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture));
            }

            _coordinator = new CapturePersistenceCoordinator(_capture, _repository, _log);
            _coordinator.Start();

            var viewModel = new MainViewModel(
                _repository,
                _capture,
                _coordinator,
                _settingsStore,
                _settings,
                _log);
            _viewModel = viewModel;
            _window = new MainWindow(viewModel, _settings);
            MainWindow = _window;
            viewModel.OperationCompleted += OperationCompleted;
            _window.HiddenToTray += MainWindowHiddenToTray;
            CreateTrayIcon(viewModel);
            ShowMainWindow();
            _log.Information(
                "main_window_presented",
                $"legacy_startup_argument={legacyStartupArgument} visible={_window.IsVisible} active={_window.IsActive} state={_window.WindowState}");
        }
        catch (Exception exception)
        {
            _log?.LogError("application_start_failed", "应用启动失败", exception);
            MessageBox.Show(
                $"InputAtlas 启动失败：{exception.Message}",
                "InputAtlas",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            await ShutdownApplicationAsync();
        }
    }

    public void ShowMainWindow()
    {
        if (_window is null)
        {
            _log?.Warning("main_window_show_skipped", "reason=window_not_initialized");
            return;
        }

        var wasVisible = _window.IsVisible;
        var previousState = _window.WindowState;
        _window.Show();
        if (_window.WindowState == WindowState.Minimized)
        {
            _window.WindowState = WindowState.Normal;
        }

        _window.Activate();
        _window.Topmost = true;
        _window.Topmost = false;
        _window.Focus();
        _log?.Information(
            "main_window_shown",
            $"was_visible={wasVisible} previous_state={previousState} active={_window.IsActive}");
        ScheduleCaptureRegistrationRefresh("main_window_shown");
    }

    public async Task ShutdownApplicationAsync()
    {
        if (_shutdownStarted)
        {
            return;
        }

        _shutdownStarted = true;
        try
        {
            _log?.Information("application_shutdown", "应用开始受控退出");
            if (_viewModel is not null)
            {
                _viewModel.OperationCompleted -= OperationCompleted;
                _viewModel = null;
            }

            if (_window is not null)
            {
                _window.HiddenToTray -= MainWindowHiddenToTray;
                _window.AllowClose();
            }

            if (_coordinator is not null)
            {
                await _coordinator.StopAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1));
            }

            if (_capture is not null)
            {
                await _capture.DisposeAsync();
            }

            if (_repository is not null)
            {
                await _repository.DisposeAsync();
            }

            if (_singleInstance is not null)
            {
                await _singleInstance.DisposeAsync();
            }

            _tray?.Dispose();
            _tray = null;
            _trayIcon?.Dispose();
            _trayIcon = null;
            if (_log is not null)
            {
                await _log.DisposeAsync();
            }
        }
        catch (Exception exception)
        {
            Debug.WriteLine(exception);
        }
        finally
        {
            Shutdown();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await ShutdownApplicationAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    private void CreateTrayIcon(MainViewModel viewModel)
    {
        _trayIcon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!)
            ?? (System.Drawing.Icon)System.Drawing.SystemIcons.Application.Clone();
        _tray = new Forms.NotifyIcon
        {
            Text = "InputAtlas 输入图谱",
            Icon = _trayIcon,
            Visible = true,
        };
        _tray.DoubleClick += (_, _) => Dispatcher.Invoke(ShowMainWindow);
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("打开", null, (_, _) => Dispatcher.Invoke(ShowMainWindow));
        menu.Items.Add("暂停/恢复", null, async (_, _) => await Dispatcher.InvokeAsync(viewModel.ToggleCaptureAsync));
        menu.Items.Add("立即保存", null, async (_, _) => await Dispatcher.InvokeAsync(async () => await viewModel.SaveNowAsync()));
        menu.Items.Add("退出", null, async (_, _) => await Dispatcher.InvokeAsync(ShutdownApplicationAsync));
        _tray.ContextMenuStrip = menu;
    }

    private void OperationCompleted(string title, string message)
    {
        _log?.Information("windows_notification_requested", $"title={title} message={message}");
        _tray?.ShowBalloonTip(3500, title, message, Forms.ToolTipIcon.Info);
    }

    private void MainWindowHiddenToTray(object? sender, EventArgs e) =>
        ScheduleCaptureRegistrationRefresh("main_window_hidden_to_tray");

    private void ScheduleCaptureRegistrationRefresh(string reason)
    {
        if (_capture is null || _shutdownStarted)
        {
            return;
        }

        Dispatcher.BeginInvoke(
            () => RefreshCaptureRegistrationAsync(reason),
            System.Windows.Threading.DispatcherPriority.ApplicationIdle);
    }

    private async void RefreshCaptureRegistrationAsync(string reason)
    {
        if (_capture is null || _shutdownStarted)
        {
            return;
        }

        try
        {
            await _capture.RefreshRegistrationAsync(reason);
        }
        catch (Exception exception)
        {
            _log?.LogError(
                "capture_registration_refresh_request_failed",
                $"Raw Input 注册刷新请求失败 reason={reason}",
                exception);
        }
    }

    private void StartSingleInstanceCommandServer()
    {
        _singleInstance!.StartServer(command =>
        {
            _log?.Information("single_instance_command_received", $"command={command}");
            Dispatcher.BeginInvoke(() =>
            {
                if (string.Equals(command, "activate", StringComparison.Ordinal))
                {
                    ShowMainWindow();
                }
                else if (string.Equals(command, "shutdown-for-update", StringComparison.Ordinal))
                {
                    _ = ShutdownApplicationAsync();
                }
                else
                {
                    _log?.Warning("single_instance_command_ignored", $"command={command}");
                }
            });
            return Task.CompletedTask;
        });
        _log?.Information("single_instance_server_started", "单实例命令服务已就绪");
    }

    private static string GetVersion() =>
        typeof(App).Assembly.GetName().Version?.ToString() ?? "0.0.0";
}
