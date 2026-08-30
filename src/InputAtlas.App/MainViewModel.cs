using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using InputAtlas.Core;
using InputAtlas.Storage;
using InputAtlas.Windows;
using Microsoft.Win32;
using OxyPlot;

namespace InputAtlas.App;

public sealed class MainViewModel : ObservableObject, IAsyncDisposable
{
    private readonly SqliteBucketRepository _repository;
    private readonly RawInputCaptureController _capture;
    private readonly CapturePersistenceCoordinator _coordinator;
    private readonly AppSettingsStore _settingsStore;
    private readonly IApplicationLog _log;
    private readonly DataManagementService _dataManagement;
    private readonly string _settingsPath;
    private readonly string _dataDirectory = Path.Combine(AppContext.BaseDirectory, "Data");
    private readonly string _versionText = $"InputAtlas {typeof(MainViewModel).Assembly.GetName().Version}";
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private AppSettings _settings;
    private CancellationTokenSource? _refreshCancellation;
    private int _pageIndex;
    private string _currentDate = string.Empty;
    private string _timeZoneLabel = string.Empty;
    private string _statusText = "正在启动";
    private string _lastSavedText = "尚未保存";
    private string _keyboardCount = "0";
    private string _mouseCount = "0";
    private string _wheelCount = "0";
    private string _activityScore = "0.0";
    private string _usedDays = "1";
    private string _activeDays = "0";
    private string _selectedInputTitle = "选择一个按键查看详情";
    private string _selectedInputDetail = "不会显示最后按下时间或输入顺序。";
    private IReadOnlyDictionary<InputId, long> _inputCounts = new ReadOnlyDictionary<InputId, long>(new Dictionary<InputId, long>());
    private KeyboardLayoutDefinition _keyboardLayout;
    private InputId? _selectedInput;
    private PlotModel _trendPlot = ChartAdapter.CreateCountsChart([], "最近 7 天输入趋势");
    private PlotModel _activityPlot = ChartAdapter.CreateActivityChart([]);
    private DateTime? _historyStart = DateTime.Today.AddDays(-6);
    private DateTime? _historyEnd = DateTime.Today;
    private string _historySummary = "选择范围后查询。";

    public MainViewModel(
        SqliteBucketRepository repository,
        RawInputCaptureController capture,
        CapturePersistenceCoordinator coordinator,
        AppSettingsStore settingsStore,
        AppSettings settings,
        IApplicationLog log)
    {
        _repository = repository;
        _capture = capture;
        _coordinator = coordinator;
        _settingsStore = settingsStore;
        _settings = settings;
        _log = log;
        _dataManagement = new DataManagementService(repository, log);
        _settingsPath = Path.Combine(AppContext.BaseDirectory, "Data", "config.json");
        _keyboardLayout = KeyboardLayoutLoader.Load(settings.KeyboardLayout);
        NavigateCommand = new RelayCommand(parameter => PageIndex = Convert.ToInt32(parameter, CultureInfo.InvariantCulture));
        RefreshCommand = new RelayCommand(async _ => await RefreshAsync());
        QueryHistoryCommand = new RelayCommand(async _ => await QueryHistoryAsync());
        _capture.StatusChanged += CaptureStatusChanged;
        _coordinator.SnapshotChanged += CoordinatorSnapshotChanged;
    }

    public RelayCommand NavigateCommand { get; }

    public RelayCommand RefreshCommand { get; }

    public RelayCommand QueryHistoryCommand { get; }

    public int PageIndex
    {
        get => _pageIndex;
        set => SetProperty(ref _pageIndex, value);
    }

    public string CurrentDate
    {
        get => _currentDate;
        private set => SetProperty(ref _currentDate, value);
    }

    public string TimeZoneLabel
    {
        get => _timeZoneLabel;
        private set => SetProperty(ref _timeZoneLabel, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string LastSavedText
    {
        get => _lastSavedText;
        private set => SetProperty(ref _lastSavedText, value);
    }

    public string KeyboardCount
    {
        get => _keyboardCount;
        private set => SetProperty(ref _keyboardCount, value);
    }

    public string MouseCount
    {
        get => _mouseCount;
        private set => SetProperty(ref _mouseCount, value);
    }

    public string WheelCount
    {
        get => _wheelCount;
        private set => SetProperty(ref _wheelCount, value);
    }

    public string ActivityScore
    {
        get => _activityScore;
        private set => SetProperty(ref _activityScore, value);
    }

    public string UsedDays
    {
        get => _usedDays;
        private set => SetProperty(ref _usedDays, value);
    }

    public string ActiveDays
    {
        get => _activeDays;
        private set => SetProperty(ref _activeDays, value);
    }

    public string SelectedInputTitle
    {
        get => _selectedInputTitle;
        private set => SetProperty(ref _selectedInputTitle, value);
    }

    public string SelectedInputDetail
    {
        get => _selectedInputDetail;
        private set => SetProperty(ref _selectedInputDetail, value);
    }

    public IReadOnlyDictionary<InputId, long> InputCounts
    {
        get => _inputCounts;
        private set => SetProperty(ref _inputCounts, value);
    }

    public KeyboardLayoutDefinition KeyboardLayout
    {
        get => _keyboardLayout;
        private set => SetProperty(ref _keyboardLayout, value);
    }

    public InputId? SelectedInput
    {
        get => _selectedInput;
        private set => SetProperty(ref _selectedInput, value);
    }

    public PlotModel TrendPlot
    {
        get => _trendPlot;
        private set => SetProperty(ref _trendPlot, value);
    }

    public PlotModel ActivityPlot
    {
        get => _activityPlot;
        private set => SetProperty(ref _activityPlot, value);
    }

    public DateTime? HistoryStart
    {
        get => _historyStart;
        set => SetProperty(ref _historyStart, value);
    }

    public DateTime? HistoryEnd
    {
        get => _historyEnd;
        set => SetProperty(ref _historyEnd, value);
    }

    public string HistorySummary
    {
        get => _historySummary;
        private set => SetProperty(ref _historySummary, value);
    }

    public string DataDirectory => _dataDirectory;

    public string VersionText => _versionText;

    public AppSettings Settings => _settings;

    public async Task InitializeAsync() => await RefreshAsync().ConfigureAwait(true);

    public async Task RefreshAsync()
    {
        if (!await _refreshGate.WaitAsync(0).ConfigureAwait(true))
        {
            return;
        }

        _refreshCancellation?.Cancel();
        _refreshCancellation?.Dispose();
        _refreshCancellation = new CancellationTokenSource();
        var cancellationToken = _refreshCancellation.Token;
        try
        {
            var started = Stopwatch.GetTimestamp();
            var (zone, fallback) = GetEffectiveTimeZone();
            var now = DateTimeOffset.UtcNow;
            var localNow = TimeZoneInfo.ConvertTime(now, zone);
            var localStart = DateTime.SpecifyKind(localNow.Date, DateTimeKind.Unspecified);
            var startUtc = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(localStart, zone)).ToUnixTimeSeconds();
            var endUtc = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(localStart.AddDays(1), zone)).ToUnixTimeSeconds();
            var snapshots = (await _repository.ReadRangeAsync(startUtc, endUtc, cancellationToken).ConfigureAwait(true))
                .ToDictionary(static bucket => bucket.BucketStartUtc);
            var active = _capture.GetCurrentSnapshot();
            if (active.BucketStartUtc >= startUtc && active.BucketStartUtc < endUtc)
            {
                snapshots[active.BucketStartUtc] = active;
            }

            var metrics = MetricsCalculator.Calculate(snapshots.Values);
            var counts = AggregateCounts(snapshots.Values);
            CurrentDate = localNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var offset = zone.GetUtcOffset(now.UtcDateTime);
            TimeZoneLabel = fallback
                ? "UTC+0 · 当前时区含非整数小时偏移，已回退"
                : $"UTC{offset.Hours:+0;-0;+0}";
            StatusText = StatusToText(_capture.Status);
            LastSavedText = _coordinator.LastSavedUtc is { } saved
                ? $"最近保存 {TimeZoneInfo.ConvertTime(saved, zone):HH:mm:ss}"
                : "尚未完成首次检查点";
            KeyboardCount = metrics.KeyboardCount.ToString("N0", CultureInfo.CurrentCulture);
            MouseCount = metrics.MouseButtonCount.ToString("N0", CultureInfo.CurrentCulture);
            WheelCount = metrics.WheelSteps.ToString("N0", CultureInfo.CurrentCulture);
            ActivityScore = metrics.ActivityScore.ToString("N1", CultureInfo.CurrentCulture);
            InputCounts = new ReadOnlyDictionary<InputId, long>(counts);

            var firstText = await _repository.GetMetadataAsync("first_capture_utc", cancellationToken).ConfigureAwait(true);
            if (long.TryParse(firstText, NumberStyles.None, CultureInfo.InvariantCulture, out var firstUtc))
            {
                var firstDate = TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeSeconds(firstUtc), zone).Date;
                UsedDays = ((localNow.Date - firstDate).Days + 1).ToString(CultureInfo.CurrentCulture);
                ActiveDays = (await CountActiveDaysAsync(firstUtc, endUtc, zone, cancellationToken).ConfigureAwait(true))
                    .ToString(CultureInfo.CurrentCulture);
            }

            UpdateSelectedDetail();
            await RefreshTrendAsync(now, cancellationToken).ConfigureAwait(true);
            _log.Information(
                "dashboard_refreshed",
                $"duration_ms={Stopwatch.GetElapsedTime(started).TotalMilliseconds:F1} buckets={snapshots.Count}");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            _log.LogError("dashboard_refresh_failed", "首页查询失败", exception);
            StatusText = "查询失败，记录仍在后台继续";
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    public void SelectInput(InputId input)
    {
        SelectedInput = input;
        UpdateSelectedDetail();
    }

    public async Task ToggleCaptureAsync()
    {
        if (_capture.Status == CaptureStatus.Paused)
        {
            await _capture.ResumeAsync();
        }
        else
        {
            await SaveNowAsync();
            await _capture.PauseAsync();
        }

        StatusText = StatusToText(_capture.Status);
    }

    public async Task SaveNowAsync()
    {
        await _coordinator.SaveNowAsync();
        LastSavedText = "正在保存…";
    }

    public async Task ChangeLayoutAsync(KeyboardLayoutKind kind)
    {
        _settings = _settings with { KeyboardLayout = kind };
        await _settingsStore.SaveAsync(_settings);
        KeyboardLayout = KeyboardLayoutLoader.Load(kind);
        SelectedInput = null;
        _log.Information("settings_layout_changed", $"layout={kind}");
    }

    public async Task SetStartWithWindowsAsync(bool enabled)
    {
        StartupRegistration.SetEnabled(enabled, Environment.ProcessPath ?? throw new InvalidOperationException("无法确定程序路径。"));
        _settings = _settings with { StartWithWindows = enabled };
        await _settingsStore.SaveAsync(_settings);
        _log.Information("settings_autostart_changed", $"enabled={enabled}");
    }

    public async Task QueryHistoryAsync()
    {
        if (HistoryStart is null || HistoryEnd is null || HistoryEnd < HistoryStart || HistoryEnd > DateTime.Today)
        {
            HistorySummary = "日期范围无效，且不能包含未来日期。";
            return;
        }

        var (zone, _) = GetEffectiveTimeZone();
        var start = DateTime.SpecifyKind(HistoryStart.Value.Date, DateTimeKind.Unspecified);
        var end = DateTime.SpecifyKind(HistoryEnd.Value.Date.AddDays(1), DateTimeKind.Unspecified);
        var startUtc = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(start, zone)).ToUnixTimeSeconds();
        var endUtc = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(end, zone)).ToUnixTimeSeconds();
        var snapshots = await _repository.ReadRangeAsync(startUtc, endUtc);
        var metrics = MetricsCalculator.Calculate(snapshots);
        var coverage = snapshots.Sum(static item => item.CoverageSeconds);
        var expected = Math.Max(1, endUtc - startUtc);
        HistorySummary = $"键盘 {metrics.KeyboardCount:N0} · 鼠标 {metrics.MouseButtonCount:N0} · 滚轮 {metrics.WheelSteps:N0} · 覆盖率 {coverage * 100d / expected:F1}%";
    }

    public async Task ExportTodayAsync()
    {
        var dialog = new SaveFileDialog
        {
            Title = "导出 InputAtlas 统计",
            Filter = "CSV 文件 (*.csv)|*.csv",
            FileName = $"InputAtlas-{DateTime.Today:yyyyMMdd}.csv",
            InitialDirectory = Path.Combine(DataDirectory, "Exports"),
            AddExtension = true,
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            await SaveNowAsync();
            var (zone, _) = GetEffectiveTimeZone();
            var (startUtc, endUtc) = GetUtcDayRange(DateTime.Today, zone);
            var rows = await _dataManagement.ExportCsvAsync(dialog.FileName, startUtc, endUtc, zone);
            MessageBox.Show($"导出完成，共 {rows:N0} 行。\n{dialog.FileName}", "InputAtlas");
        }
        catch (Exception exception)
        {
            _log.LogError("export_failed", "用户导出失败", exception);
            MessageBox.Show($"导出失败：{exception.Message}", "InputAtlas", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    public async Task CreateBackupAsync()
    {
        var directory = Path.Combine(DataDirectory, "Backups");
        Directory.CreateDirectory(directory);
        var dialog = new SaveFileDialog
        {
            Title = "创建 InputAtlas 备份",
            Filter = "InputAtlas 备份 (*.zip)|*.zip",
            FileName = $"InputAtlas-backup-{DateTime.Now:yyyyMMdd-HHmmss}.zip",
            InitialDirectory = directory,
            AddExtension = true,
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            await SaveNowAsync();
            var path = await _dataManagement.CreateBackupPackageAsync(
                dialog.FileName,
                _settingsPath,
                typeof(MainViewModel).Assembly.GetName().Version?.ToString() ?? "0.0.0");
            MessageBox.Show($"备份完成：\n{path}", "InputAtlas");
        }
        catch (Exception exception)
        {
            _log.LogError("backup_failed", "用户备份失败", exception);
            MessageBox.Show($"备份失败：{exception.Message}", "InputAtlas", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    public async Task RestoreBackupAsync()
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择 InputAtlas 备份",
            Filter = "InputAtlas 备份 (*.zip)|*.zip",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        if (MessageBox.Show(
            "恢复将完整替换当前统计。程序会先创建当前数据的安全备份，是否继续？",
            "恢复备份",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        var wasRecording = _capture.Status == CaptureStatus.Recording;
        try
        {
            await _capture.PauseAsync();
            await SaveNowAsync();
            var safetyDirectory = Path.Combine(DataDirectory, "Backups");
            Directory.CreateDirectory(safetyDirectory);
            await _dataManagement.CreateBackupPackageAsync(
                Path.Combine(safetyDirectory, $"before-restore-{DateTime.Now:yyyyMMdd-HHmmss}.zip"),
                _settingsPath,
                typeof(MainViewModel).Assembly.GetName().Version?.ToString() ?? "0.0.0");
            await _dataManagement.RestoreBackupPackageAsync(dialog.FileName, _settingsPath);
            await RefreshAsync();
            MessageBox.Show("恢复完成，统计已重新载入。", "InputAtlas");
        }
        catch (Exception exception)
        {
            _log.LogError("restore_failed", "用户恢复失败", exception);
            MessageBox.Show($"恢复失败，原数据已保留或回滚：{exception.Message}", "InputAtlas", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            if (wasRecording)
            {
                await _capture.ResumeAsync();
            }
        }
    }

    public async Task DeleteSelectedHistoryAsync()
    {
        if (HistoryStart is null || HistoryEnd is null || HistoryEnd < HistoryStart || HistoryEnd > DateTime.Today)
        {
            MessageBox.Show("请先在历史页选择有效日期范围。", "InputAtlas");
            return;
        }

        if (MessageBox.Show(
            $"将删除 {HistoryStart:yyyy-MM-dd} 至 {HistoryEnd:yyyy-MM-dd} 的统计。删除前会自动备份，是否继续？",
            "删除统计",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            var backupDirectory = Path.Combine(DataDirectory, "Backups");
            Directory.CreateDirectory(backupDirectory);
            await SaveNowAsync();
            await _dataManagement.CreateBackupPackageAsync(
                Path.Combine(backupDirectory, $"before-delete-{DateTime.Now:yyyyMMdd-HHmmss}.zip"),
                _settingsPath,
                typeof(MainViewModel).Assembly.GetName().Version?.ToString() ?? "0.0.0");
            var (zone, _) = GetEffectiveTimeZone();
            var start = GetUtcDayRange(HistoryStart.Value.Date, zone).StartUtc;
            var end = GetUtcDayRange(HistoryEnd.Value.Date, zone).EndUtc;
            var affected = await _repository.DeleteRangeAsync(start, end);
            _log.Information("statistics_deleted", $"rows={affected}");
            await QueryHistoryAsync();
            await RefreshAsync();
            MessageBox.Show($"删除完成，影响 {affected} 个统计桶。安全备份保存在 Backups 目录。", "InputAtlas");
        }
        catch (Exception exception)
        {
            _log.LogError("statistics_delete_failed", "范围删除失败", exception);
            MessageBox.Show($"删除失败：{exception.Message}", "InputAtlas", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _capture.StatusChanged -= CaptureStatusChanged;
        _coordinator.SnapshotChanged -= CoordinatorSnapshotChanged;
        _refreshCancellation?.Cancel();
        _refreshCancellation?.Dispose();
        _refreshGate.Dispose();
        await Task.CompletedTask;
    }

    private async Task RefreshTrendAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var start = now.AddDays(-7).ToUnixTimeSeconds();
        var end = now.ToUnixTimeSeconds() + 1;
        var granularity = TimeBuckets.ChooseGranularity(TimeSpan.FromDays(7));
        var keyboard = await new StatisticsQueryService(_repository).QueryAsync(
            new StatisticsQuery(start, end, granularity, InputCategory.Keyboard),
            cancellationToken).ConfigureAwait(true);
        TrendPlot = ChartAdapter.CreateCountsChart(keyboard, "最近 7 天键盘趋势");
        ActivityPlot = ChartAdapter.CreateActivityChart(keyboard);
    }

    private async Task<int> CountActiveDaysAsync(
        long firstUtc,
        long endUtc,
        TimeZoneInfo zone,
        CancellationToken cancellationToken)
    {
        var snapshots = await _repository.ReadRangeAsync(firstUtc, endUtc, cancellationToken).ConfigureAwait(true);
        var byDay = snapshots.GroupBy(bucket =>
            TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeSeconds(bucket.BucketStartUtc), zone).Date);
        return byDay.Count(group => MetricsCalculator.Calculate(group).IsActiveDay);
    }

    private void UpdateSelectedDetail()
    {
        if (SelectedInput is not { } input)
        {
            SelectedInputTitle = "选择一个按键查看详情";
            SelectedInputDetail = "不会显示最后按下时间或输入顺序。";
            return;
        }

        var key = KeyboardLayout.Keys.FirstOrDefault(item => item.Input == input);
        var label = key?.Label ?? InputDisplayName(input);
        InputCounts.TryGetValue(input, out var count);
        SelectedInputTitle = label;
        SelectedInputDetail = key is { Observable: false }
            ? "该键通常由键盘固件处理，Windows Raw Input 无法可靠观测。"
            : $"今日 {count:N0} 次 · 仅保存聚合计数";
    }

    private void CoordinatorSnapshotChanged()
    {
        if (Application.Current?.Dispatcher is { } dispatcher)
        {
            _ = dispatcher.InvokeAsync(RefreshAsync);
        }
    }

    private void CaptureStatusChanged(object? sender, CaptureStatus status)
    {
        if (Application.Current?.Dispatcher is { } dispatcher)
        {
            _ = dispatcher.InvokeAsync(() => StatusText = StatusToText(status));
        }
    }

    private static SortedDictionary<InputId, long> AggregateCounts(IEnumerable<BucketSnapshot> snapshots)
    {
        var result = new SortedDictionary<InputId, long>();
        foreach (var snapshot in snapshots)
        {
            foreach (var pair in snapshot.Counts)
            {
                result.TryGetValue(pair.Key, out var current);
                result[pair.Key] = current > long.MaxValue - pair.Value ? long.MaxValue : current + pair.Value;
            }
        }

        return result;
    }

    private static (TimeZoneInfo Zone, bool Fallback) GetEffectiveTimeZone()
    {
        var local = TimeZoneInfo.Local;
        var offset = local.GetUtcOffset(DateTime.UtcNow);
        return offset.Ticks % TimeSpan.TicksPerHour == 0 ? (local, false) : (TimeZoneInfo.Utc, true);
    }

    private static (long StartUtc, long EndUtc) GetUtcDayRange(DateTime date, TimeZoneInfo zone)
    {
        var localStart = DateTime.SpecifyKind(date.Date, DateTimeKind.Unspecified);
        return (
            new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(localStart, zone)).ToUnixTimeSeconds(),
            new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(localStart.AddDays(1), zone)).ToUnixTimeSeconds());
    }

    private static string StatusToText(CaptureStatus status) => status switch
    {
        CaptureStatus.Starting => "正在启动记录",
        CaptureStatus.Recording => "正在记录",
        CaptureStatus.Paused => "记录已暂停",
        CaptureStatus.Unavailable => "Raw Input 不可用",
        CaptureStatus.FaultBuffering => "数据库故障，内存缓冲中",
        CaptureStatus.Stopped => "记录已停止",
        _ => "未知状态",
    };

    private static string InputDisplayName(InputId input) => input.Value switch
    {
        1001 => "鼠标左键",
        1002 => "鼠标右键",
        1003 => "鼠标中键",
        1004 => "鼠标后侧键",
        1005 => "鼠标前侧键",
        1011 => "滚轮向上",
        1012 => "滚轮向下",
        1013 => "横向滚轮向左",
        1014 => "横向滚轮向右",
        _ => $"输入项 {input.Value}",
    };
}
