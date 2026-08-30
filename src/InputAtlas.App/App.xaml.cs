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
    private MainWindow? _window;
    private Forms.NotifyIcon? _tray;
    private bool _shutdownStarted;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            _singleInstance = new SingleInstanceCoordinator();
            if (!_singleInstance.TryAcquire())
            {
                await _singleInstance.SendCommandAsync("activate", TimeSpan.FromSeconds(1));
                Shutdown();
                return;
            }

            var dataRoot = Path.Combine(AppContext.BaseDirectory, "Data");
            var logRoot = Path.Combine(dataRoot, "Logs");
            _log = new AsyncRollingLog(logRoot);
            _log.Information(
                "application_start",
                $"version={GetVersion()} architecture={RuntimeInformation.ProcessArchitecture} runtime={Environment.Version}");

            _settingsStore = new AppSettingsStore(Path.Combine(dataRoot, "config.json"));
            _settings = await _settingsStore.LoadAsync();
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
            _window = new MainWindow(viewModel, _settings);
            MainWindow = _window;
            _window.Show();
            CreateTrayIcon(viewModel);

            _singleInstance.StartServer(command => Dispatcher.InvokeAsync(async () =>
            {
                if (string.Equals(command, "activate", StringComparison.Ordinal))
                {
                    ShowMainWindow();
                }
                else if (string.Equals(command, "shutdown-for-update", StringComparison.Ordinal))
                {
                    await ShutdownApplicationAsync();
                }
            }).Task.Unwrap());

            if (e.Args.Contains("--startup", StringComparer.OrdinalIgnoreCase))
            {
                _window.Hide();
            }
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
            return;
        }

        _window.Show();
        if (_window.WindowState == WindowState.Minimized)
        {
            _window.WindowState = WindowState.Normal;
        }

        _window.Activate();
        _window.Topmost = true;
        _window.Topmost = false;
        _window.Focus();
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
            if (_window is not null)
            {
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
        _tray = new Forms.NotifyIcon
        {
            Text = "InputAtlas 输入图谱",
            Icon = System.Drawing.SystemIcons.Application,
            Visible = true,
        };
        _tray.DoubleClick += (_, _) => Dispatcher.Invoke(ShowMainWindow);
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("打开", null, (_, _) => Dispatcher.Invoke(ShowMainWindow));
        menu.Items.Add("暂停/恢复", null, async (_, _) => await Dispatcher.InvokeAsync(viewModel.ToggleCaptureAsync));
        menu.Items.Add("立即保存", null, async (_, _) => await Dispatcher.InvokeAsync(viewModel.SaveNowAsync));
        menu.Items.Add("退出", null, async (_, _) => await Dispatcher.InvokeAsync(ShutdownApplicationAsync));
        _tray.ContextMenuStrip = menu;
    }

    private static string GetVersion() =>
        typeof(App).Assembly.GetName().Version?.ToString() ?? "0.0.0";
}
