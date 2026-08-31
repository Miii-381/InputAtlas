using System.Collections.ObjectModel;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
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
    private readonly ConcurrentQueue<(InputId Input, bool IsPressed)> _pendingVisualTransitions = new();
    private readonly IReadOnlyList<FontFamilyOption> _fontFamilyOptions = FontFamilyService.Options;
    private AppSettings _settings;
    private CancellationTokenSource? _refreshCancellation;
    private int _pageIndex;
    private string _currentDate = string.Empty;
    private string _currentTime = string.Empty;
    private string _timeZoneLabel = string.Empty;
    private string _statusText = "正在启动";
    private CaptureStatus _captureStatus;
    private string _lastSavedText = "尚未保存";
    private string _keyboardCount = "0";
    private string _mouseCount = "0";
    private string _wheelCount = "0";
    private string _activityScore = "0.0";
    private decimal _activityScoreValue;
    private Brush _activityScoreBackground = new SolidColorBrush(Color.FromRgb(255, 252, 246));
    private Brush _activityScoreForeground = new SolidColorBrush(Color.FromRgb(29, 27, 32));
    private HeatmapThresholds _heatmapThresholds;
    private string _heatmapCoolThresholdText = "100";
    private string _heatmapWarmThresholdText = "500";
    private string _heatmapHotThresholdText = "2000";
    private string _heatmapThresholdStatus = "单键采用当前阈值；活跃分数自动使用 10 倍阈值。";
    private HeatmapThresholdMode _heatmapThresholdMode;
    private string _themeColorHexText = ThemeColorService.DefaultAccentColor;
    private string _themeColorStatus = "选择色块或输入六位十六进制颜色。";
    private string _fontFamily = FontFamilyService.DefaultFontFamily;
    private string _usedDays = "1";
    private string _activeDays = "0";
    private string _selectedInputTitle = "选择一个按键查看详情";
    private string _selectedInputDetail = "不会显示最后按下时间或输入顺序。";
    private IReadOnlyDictionary<InputId, long> _inputCounts = new ReadOnlyDictionary<InputId, long>(new Dictionary<InputId, long>());
    private KeyboardLayoutDefinition _keyboardLayout;
    private InputId? _selectedInput;
    private PlotModel _trendPlot = ChartAdapter.CreateCountsChart(
        [],
        "最近 7 天键盘趋势",
        GetEffectiveTimeZone().Zone);
    private PlotModel _activityPlot = ChartAdapter.CreateActivityChart([], GetEffectiveTimeZone().Zone);
    private PlotModel _keyDistributionPlot = ChartAdapter.CreateKeyDistributionChart([]);
    private PlotModel _categoryDistributionPlot = ChartAdapter.CreateCategoryDistributionChart([]);
    private IReadOnlyList<InputRankingItem> _inputLeaderboard = [];
    private IReadOnlyList<CategorySummaryItem> _categorySummaries = [];
    private DateTime? _historyStart = DateTime.Today.AddDays(-6);
    private DateTime? _historyEnd = DateTime.Today;
    private string _historySummary = "选择范围后查询。";
    private string _historyKeyboardCount = "0";
    private string _historyMouseCount = "0";
    private string _historyWheelCount = "0";
    private string _historyActivityScore = "0.0";
    private string _historyCoverage = "实时";
    private string _analysisScopeLabel = "今天（实时）";
    private bool _analysisUsesHistoryScope;
    private long? _analysisRangeStartUtc;
    private long? _analysisRangeEndUtc;
    private IReadOnlyDictionary<InputId, long> _analysisCounts =
        new ReadOnlyDictionary<InputId, long>(new Dictionary<InputId, long>());
    private IReadOnlyDictionary<InputId, long> _todayHistoricalCounts =
        new ReadOnlyDictionary<InputId, long>(new Dictionary<InputId, long>());
    private long _liveDayStartUtc;
    private long _liveDayEndUtc;
    private int _liveFrameScheduled;
    private int _liveCountDirty;
    private long _liveEventsSinceLog;
    private long _lastLiveFrameLogTimestamp = Stopwatch.GetTimestamp();

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
        _captureStatus = capture.Status;
        _log = log;
        _dataManagement = new DataManagementService(repository, log);
        _settingsPath = Path.Combine(AppContext.BaseDirectory, "Data", "config.json");
        _keyboardLayout = KeyboardLayoutLoader.Load(settings.KeyboardLayout);
        _heatmapThresholds = new HeatmapThresholds(
            settings.HeatmapCoolThreshold,
            settings.HeatmapWarmThreshold,
            settings.HeatmapHotThreshold);
        _heatmapCoolThresholdText = settings.HeatmapCoolThreshold.ToString(CultureInfo.InvariantCulture);
        _heatmapWarmThresholdText = settings.HeatmapWarmThreshold.ToString(CultureInfo.InvariantCulture);
        _heatmapHotThresholdText = settings.HeatmapHotThreshold.ToString(CultureInfo.InvariantCulture);
        _heatmapThresholdMode = settings.HeatmapThresholdMode;
        _themeColorHexText = ThemeColorService.TryNormalize(settings.AccentColor, out var normalizedAccent)
            ? normalizedAccent
            : ThemeColorService.DefaultAccentColor;
        _fontFamily = FontFamilyService.Normalize(settings.FontFamily);
        NavigateCommand = new RelayCommand(parameter => PageIndex = Convert.ToInt32(parameter, CultureInfo.InvariantCulture));
        RefreshCommand = new RelayCommand(async _ => await RefreshAsync());
        QueryHistoryCommand = new RelayCommand(async _ => await QueryHistoryAsync());
        _capture.StatusChanged += CaptureStatusChanged;
        _capture.InputCounted += CaptureInputCounted;
        _capture.InputStateChanged += CaptureInputStateChanged;
        _coordinator.SnapshotChanged += CoordinatorSnapshotChanged;
    }

    public event Action<InputId, bool>? InputVisualStateChanged;

    /// <summary>
    /// 通知宿主应用显示一次操作完成提示（通常为 Windows 托盘气泡通知）。
    /// </summary>
    public event Action<string, string>? OperationCompleted;

    public RelayCommand NavigateCommand { get; }

    public RelayCommand RefreshCommand { get; }

    public RelayCommand QueryHistoryCommand { get; }

    public int PageIndex
    {
        get => _pageIndex;
        set
        {
            if (SetProperty(ref _pageIndex, value))
            {
                RaisePropertyChanged(nameof(PageTitle));
                RaisePropertyChanged(nameof(PageSubtitle));
            }
        }
    }

    public string PageTitle => PageIndex switch
    {
        0 => "今日概览",
        1 => "分析",
        2 => "设置",
        _ => "InputAtlas",
    };

    public string PageSubtitle => PageIndex switch
    {
        0 => "实时查看今天的输入活跃度",
        1 => "查看趋势，并按日期范围聚合本地统计",
        2 => "管理记录、外观与本地数据",
        _ => "完全离线的输入统计",
    };

    public bool IsAnsiLayout => _settings.KeyboardLayout == KeyboardLayoutKind.Ansi104;

    public bool IsCompactLayout => _settings.KeyboardLayout == KeyboardLayoutKind.Compact75;

    public bool IsCapturePaused => _captureStatus == CaptureStatus.Paused;

    public bool IsCaptureRecording => _captureStatus == CaptureStatus.Recording;

    public bool CanToggleCapture => IsCapturePaused || IsCaptureRecording;

    public string CaptureActionText => IsCapturePaused ? "恢复记录" : "暂停记录";

    public string CaptureControlHint => IsCapturePaused
        ? "记录当前已暂停；恢复后将继续累计输入次数。"
        : "记录正在运行；暂停前会先保存当前统计。";

    public string CurrentDate
    {
        get => _currentDate;
        private set => SetProperty(ref _currentDate, value);
    }

    public string CurrentTime
    {
        get => _currentTime;
        private set => SetProperty(ref _currentTime, value);
    }

    public string ActivityScoreFormula { get; } = "活跃分数 = 键盘 + 鼠标 + 滚轮 × 0.1";

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

    public Brush ActivityScoreBackground
    {
        get => _activityScoreBackground;
        private set => SetProperty(ref _activityScoreBackground, value);
    }

    public Brush ActivityScoreForeground
    {
        get => _activityScoreForeground;
        private set => SetProperty(ref _activityScoreForeground, value);
    }

    public HeatmapThresholds HeatmapThresholds
    {
        get => _heatmapThresholds;
        private set => SetProperty(ref _heatmapThresholds, value);
    }

    public HeatmapThresholds ScoreHeatmapThresholds => HeatmapThresholds.Scale(10);

    public string ScoreHeatmapThresholdSummary =>
        $"活跃分数阈值：{ScoreHeatmapThresholds.Cool:N0} / {ScoreHeatmapThresholds.Warm:N0} / {ScoreHeatmapThresholds.Hot:N0}";

    public HeatmapThresholdMode HeatmapThresholdMode
    {
        get => _heatmapThresholdMode;
        private set => SetProperty(ref _heatmapThresholdMode, value);
    }

    public IReadOnlyList<HeatmapThresholdModeOption> HeatmapThresholdModeOptions { get; } =
    [
        new(HeatmapThresholdMode.FixedCount, "固定次数", "按设置中的绝对次数分段"),
        new(HeatmapThresholdMode.RelativeToMaximum, "最高值比例", "按当前图中最高次数的比例分段"),
        new(HeatmapThresholdMode.Percentile, "数据分位数", "按当前数据的 P50 / P75 / P90 分段"),
        new(HeatmapThresholdMode.SquareRootScale, "平方根色阶", "保持固定阈值，但低频段过渡更明显"),
    ];

    public string HeatmapThresholdModeDescription => HeatmapThresholdMode switch
    {
        HeatmapThresholdMode.RelativeToMaximum => "按当前图中最高次数动态计算",
        HeatmapThresholdMode.Percentile => "按当前数据分布的 P50 / P75 / P90 动态计算",
        HeatmapThresholdMode.SquareRootScale => $"平方根色阶：固定次数 {HeatmapThresholds.Cool:N0} / {HeatmapThresholds.Warm:N0} / {HeatmapThresholds.Hot:N0}",
        _ => $"固定次数：{HeatmapThresholds.Cool:N0} / {HeatmapThresholds.Warm:N0} / {HeatmapThresholds.Hot:N0}",
    };

    public string HeatmapCoolThresholdText
    {
        get => _heatmapCoolThresholdText;
        set => SetProperty(ref _heatmapCoolThresholdText, value);
    }

    public string HeatmapWarmThresholdText
    {
        get => _heatmapWarmThresholdText;
        set => SetProperty(ref _heatmapWarmThresholdText, value);
    }

    public string HeatmapHotThresholdText
    {
        get => _heatmapHotThresholdText;
        set => SetProperty(ref _heatmapHotThresholdText, value);
    }

    public string HeatmapThresholdStatus
    {
        get => _heatmapThresholdStatus;
        private set => SetProperty(ref _heatmapThresholdStatus, value);
    }

    public string ThemeColorHexText
    {
        get => _themeColorHexText;
        set => SetProperty(ref _themeColorHexText, value);
    }

    public string ThemeColorStatus
    {
        get => _themeColorStatus;
        private set => SetProperty(ref _themeColorStatus, value);
    }

    public string FontFamily
    {
        get => _fontFamily;
        private set => SetProperty(ref _fontFamily, value);
    }

    public IReadOnlyList<FontFamilyOption> FontFamilyOptions => _fontFamilyOptions;

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

    public PlotModel KeyDistributionPlot
    {
        get => _keyDistributionPlot;
        private set => SetProperty(ref _keyDistributionPlot, value);
    }

    public PlotModel CategoryDistributionPlot
    {
        get => _categoryDistributionPlot;
        private set => SetProperty(ref _categoryDistributionPlot, value);
    }

    public IReadOnlyList<InputRankingItem> InputLeaderboard
    {
        get => _inputLeaderboard;
        private set => SetProperty(ref _inputLeaderboard, value);
    }

    public IReadOnlyList<CategorySummaryItem> CategorySummaries
    {
        get => _categorySummaries;
        private set => SetProperty(ref _categorySummaries, value);
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

    public string HistoryKeyboardCount
    {
        get => _historyKeyboardCount;
        private set => SetProperty(ref _historyKeyboardCount, value);
    }

    public string HistoryMouseCount
    {
        get => _historyMouseCount;
        private set => SetProperty(ref _historyMouseCount, value);
    }

    public string HistoryWheelCount
    {
        get => _historyWheelCount;
        private set => SetProperty(ref _historyWheelCount, value);
    }

    public string HistoryActivityScore
    {
        get => _historyActivityScore;
        private set => SetProperty(ref _historyActivityScore, value);
    }

    public string HistoryCoverage
    {
        get => _historyCoverage;
        private set => SetProperty(ref _historyCoverage, value);
    }

    public string AnalysisScopeLabel
    {
        get => _analysisScopeLabel;
        private set => SetProperty(ref _analysisScopeLabel, value);
    }

    public string DataDirectory => _dataDirectory;

    public string VersionText => _versionText;

    public AppSettings Settings => _settings;

    public void ReportDisplayScale(double scaleX, double scaleY)
    {
        _log.Information(
            "display_scale_detected",
            $"scale_x={scaleX.ToString("F2", CultureInfo.InvariantCulture)} scale_y={scaleY.ToString("F2", CultureInfo.InvariantCulture)}");
    }

    public void ReportWindowMetrics(
        double scaleX,
        double scaleY,
        double width,
        double height,
        double workWidth,
        double workHeight)
    {
        _log.Information(
            "window_dpi_layout_applied",
            $"scale_x={scaleX.ToString("F2", CultureInfo.InvariantCulture)} scale_y={scaleY.ToString("F2", CultureInfo.InvariantCulture)} " +
            $"window_dip={width.ToString("F0", CultureInfo.InvariantCulture)}x{height.ToString("F0", CultureInfo.InvariantCulture)} " +
            $"work_dip={workWidth.ToString("F0", CultureInfo.InvariantCulture)}x{workHeight.ToString("F0", CultureInfo.InvariantCulture)}");
    }

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
            _liveDayStartUtc = startUtc;
            _liveDayEndUtc = endUtc;
            _todayHistoricalCounts = new ReadOnlyDictionary<InputId, long>(
                AggregateCounts(snapshots
                    .Where(pair => pair.Key != active.BucketStartUtc)
                    .Select(static pair => pair.Value)));
            if (active.BucketStartUtc >= startUtc && active.BucketStartUtc < endUtc)
            {
                snapshots[active.BucketStartUtc] = active;
            }

            var metrics = MetricsCalculator.Calculate(snapshots.Values);
            var counts = AggregateCounts(snapshots.Values);
            CurrentDate = localNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            CurrentTime = localNow.ToString("HH:mm", CultureInfo.InvariantCulture);
            var offset = zone.GetUtcOffset(now.UtcDateTime);
            TimeZoneLabel = fallback
                ? "UTC+0 · 当前时区含非整数小时偏移，已回退"
                : $"UTC{offset.Hours:+0;-0;+0}";
            ApplyCaptureStatus(_capture.Status);
            LastSavedText = _coordinator.LastSavedUtc is { } saved
                ? $"最近保存 {TimeZoneInfo.ConvertTime(saved, zone):HH:mm:ss}"
                : "尚未完成首次检查点";
            ApplyLiveMetrics(metrics, counts);

            var firstText = await _repository.GetMetadataAsync("first_capture_utc", cancellationToken).ConfigureAwait(true);
            if (long.TryParse(firstText, NumberStyles.None, CultureInfo.InvariantCulture, out var firstUtc))
            {
                var firstDate = TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeSeconds(firstUtc), zone).Date;
                UsedDays = ((localNow.Date - firstDate).Days + 1).ToString(CultureInfo.CurrentCulture);
                ActiveDays = (await CountActiveDaysAsync(firstUtc, endUtc, zone, cancellationToken).ConfigureAwait(true))
                    .ToString(CultureInfo.CurrentCulture);
            }

            UpdateSelectedDetail();
            if (!_analysisUsesHistoryScope)
            {
                await RefreshTrendAsync(now, cancellationToken).ConfigureAwait(true);
            }
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

    public void ClearSelectedInput()
    {
        if (SelectedInput is null)
        {
            return;
        }

        SelectedInput = null;
        UpdateSelectedDetail();
    }

    public async Task ToggleCaptureAsync()
    {
        var action = _capture.Status == CaptureStatus.Paused ? "resume" : "pause";
        if (_capture.Status == CaptureStatus.Paused)
        {
            await _capture.ResumeAsync();
        }
        else
        {
            await SaveNowAsync(false);
            await _capture.PauseAsync();
        }

        ApplyCaptureStatus(_capture.Status);
        _log.Information("capture_toggle_completed", $"action={action} status={_capture.Status}");
    }

    public async Task SaveNowAsync(bool notify = true)
    {
        await _coordinator.SaveNowAsync();
        LastSavedText = "正在保存…";
        _log.Information("statistics_save_completed", $"notify={notify}");
        if (notify)
        {
            NotifyOperationCompleted("保存完成", "当前统计已保存。");
        }
    }

    public async Task ChangeLayoutAsync(KeyboardLayoutKind kind)
    {
        if (_settings.KeyboardLayout == kind)
        {
            _log.Information("settings_layout_unchanged", $"layout={kind}");
            return;
        }

        _settings = _settings with { KeyboardLayout = kind };
        await _settingsStore.SaveAsync(_settings);
        KeyboardLayout = KeyboardLayoutLoader.Load(kind);
        SelectedInput = null;
        UpdateStatisticsDimensions(_analysisCounts);
        RaisePropertyChanged(nameof(IsAnsiLayout));
        RaisePropertyChanged(nameof(IsCompactLayout));
        _log.Information("settings_layout_changed", $"layout={kind}");
    }

    public async Task SetStartWithWindowsAsync(bool enabled)
    {
        StartupRegistration.SetEnabled(enabled, Environment.ProcessPath ?? throw new InvalidOperationException("无法确定程序路径。"));
        _settings = _settings with { StartWithWindows = enabled };
        await _settingsStore.SaveAsync(_settings);
        _log.Information("settings_autostart_changed", $"enabled={enabled}");
    }

    public async Task ApplyHeatmapThresholdsAsync()
    {
        if (!TryParsePositiveLong(HeatmapCoolThresholdText, out var cool) ||
            !TryParsePositiveLong(HeatmapWarmThresholdText, out var warm) ||
            !TryParsePositiveLong(HeatmapHotThresholdText, out var hot))
        {
            HeatmapThresholdStatus = "请输入大于 0 的整数。";
            _log.Information("settings_heatmap_thresholds_rejected", "reason=invalid_number");
            return;
        }

        var thresholds = new HeatmapThresholds(cool, warm, hot);
        if (!thresholds.IsValid)
        {
            HeatmapThresholdStatus = "阈值必须满足：冷色 < 暖色 < 红色。";
            _log.Information("settings_heatmap_thresholds_rejected", "reason=invalid_order");
            return;
        }

        _settings = _settings with
        {
            HeatmapCoolThreshold = cool,
            HeatmapWarmThreshold = warm,
            HeatmapHotThreshold = hot,
        };
        await _settingsStore.SaveAsync(_settings);
        HeatmapThresholds = thresholds;
        RaisePropertyChanged(nameof(ScoreHeatmapThresholds));
        RaisePropertyChanged(nameof(ScoreHeatmapThresholdSummary));
        RaisePropertyChanged(nameof(HeatmapThresholdModeDescription));
        UpdateActivityScoreHeat();
        HeatmapThresholdStatus = $"单键已应用：{cool:N0} / {warm:N0} / {hot:N0}；分数自动 ×10。";
        _log.Information("settings_heatmap_thresholds_changed", $"cool={cool} warm={warm} hot={hot}");
    }

    public async Task ApplyHeatmapThresholdModeAsync(HeatmapThresholdMode mode)
    {
        if (!Enum.IsDefined(mode))
        {
            _log.Information("settings_heatmap_mode_rejected", $"mode={mode}");
            return;
        }

        if (HeatmapThresholdMode == mode)
        {
            return;
        }

        _settings = _settings with { HeatmapThresholdMode = mode };
        await _settingsStore.SaveAsync(_settings);
        HeatmapThresholdMode = mode;
        RaisePropertyChanged(nameof(HeatmapThresholdModeDescription));
        _log.Information("settings_heatmap_mode_changed", $"mode={mode}");
        UpdateActivityScoreHeat();
    }

    public async Task<bool> ApplyThemeColorAsync(string? requestedColor = null)
    {
        var candidate = requestedColor ?? ThemeColorHexText;
        if (!ThemeColorService.TryNormalize(candidate, out var normalized))
        {
            ThemeColorStatus = "颜色格式无效，请输入例如 #F3D48D。";
            _log.Information("settings_accent_color_rejected", "reason=invalid_hex");
            return false;
        }

        ThemeColorHexText = normalized;
        ThemeColorService.Apply(normalized);
        _settings = _settings with { AccentColor = normalized };
        await _settingsStore.SaveAsync(_settings);
        ThemeColorStatus = $"主题色已应用：{normalized}";
        _log.Information("settings_accent_color_changed", $"accent={normalized}");
        return true;
    }

    public async Task ApplyFontFamilyAsync(string? requestedFontFamily)
    {
        var normalized = FontFamilyService.Normalize(requestedFontFamily);
        if (string.Equals(FontFamily, normalized, StringComparison.Ordinal))
        {
            return;
        }

        var appliedFontFamily = FontFamilyService.Apply(normalized);
        _settings = _settings with { FontFamily = normalized };
        await _settingsStore.SaveAsync(_settings);
        FontFamily = normalized;
        _log.Information(
            "settings_font_family_changed",
            $"font_family={normalized} source={appliedFontFamily.Source} windows={Application.Current?.Windows.Count ?? 0}");
    }

    public async Task QueryHistoryAsync()
    {
        if (HistoryStart is null || HistoryEnd is null || HistoryEnd < HistoryStart || HistoryEnd > DateTime.Today)
        {
            HistorySummary = "日期范围无效，且不能包含未来日期。";
            _log.Information("history_query_rejected", "reason=invalid_date_range");
            return;
        }

        var (zone, _) = GetEffectiveTimeZone();
        var start = DateTime.SpecifyKind(HistoryStart.Value.Date, DateTimeKind.Unspecified);
        var end = DateTime.SpecifyKind(HistoryEnd.Value.Date.AddDays(1), DateTimeKind.Unspecified);
        var startUtc = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(start, zone)).ToUnixTimeSeconds();
        var endUtc = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(end, zone)).ToUnixTimeSeconds();
        var snapshots = await _repository.ReadRangeAsync(startUtc, endUtc);
        var metrics = MetricsCalculator.Calculate(snapshots);
        var counts = AggregateCounts(snapshots);
        var coverage = snapshots.Sum(static item => item.CoverageSeconds);
        var expected = Math.Max(1, endUtc - startUtc);
        HistorySummary = $"键盘 {metrics.KeyboardCount:N0} · 鼠标 {metrics.MouseButtonCount:N0} · 滚轮 {metrics.WheelSteps:N0} · 覆盖率 {coverage * 100d / expected:F1}%";
        HistoryKeyboardCount = metrics.KeyboardCount.ToString("N0", CultureInfo.CurrentCulture);
        HistoryMouseCount = metrics.MouseButtonCount.ToString("N0", CultureInfo.CurrentCulture);
        HistoryWheelCount = metrics.WheelSteps.ToString("N0", CultureInfo.CurrentCulture);
        HistoryActivityScore = metrics.ActivityScore.ToString("N1", CultureInfo.CurrentCulture);
        HistoryCoverage = $"覆盖率 {coverage * 100d / expected:F1}%";
        AnalysisScopeLabel = $"{HistoryStart.Value:yyyy-MM-dd} 至 {HistoryEnd.Value:yyyy-MM-dd}";
        _analysisUsesHistoryScope = true;
        _analysisRangeStartUtc = startUtc;
        _analysisRangeEndUtc = endUtc;
        _analysisCounts = new ReadOnlyDictionary<InputId, long>(new Dictionary<InputId, long>(counts));
        UpdateStatisticsDimensions(_analysisCounts);
        await RefreshTrendAsync(DateTimeOffset.UtcNow, CancellationToken.None).ConfigureAwait(true);
        _log.Information(
            "history_dashboard_refreshed",
            $"start_utc={startUtc} end_utc={endUtc} buckets={snapshots.Count} keyboard={metrics.KeyboardCount} mouse={metrics.MouseButtonCount} wheel={metrics.WheelSteps}");
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
            await SaveNowAsync(false);
            var (zone, _) = GetEffectiveTimeZone();
            var (startUtc, endUtc) = GetUtcDayRange(DateTime.Today, zone);
            var rows = await _dataManagement.ExportCsvAsync(dialog.FileName, startUtc, endUtc, zone);
            NotifyOperationCompleted("导出完成", $"已导出 {rows:N0} 行统计。文件：{Path.GetFileName(dialog.FileName)}");
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
            await SaveNowAsync(false);
            var path = await _dataManagement.CreateBackupPackageAsync(
                dialog.FileName,
                _settingsPath,
                typeof(MainViewModel).Assembly.GetName().Version?.ToString() ?? "0.0.0");
            NotifyOperationCompleted("备份完成", $"备份文件已创建：{Path.GetFileName(path)}");
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
            await SaveNowAsync(false);
            var safetyDirectory = Path.Combine(DataDirectory, "Backups");
            Directory.CreateDirectory(safetyDirectory);
            await _dataManagement.CreateBackupPackageAsync(
                Path.Combine(safetyDirectory, $"before-restore-{DateTime.Now:yyyyMMdd-HHmmss}.zip"),
                _settingsPath,
                typeof(MainViewModel).Assembly.GetName().Version?.ToString() ?? "0.0.0");
            await _dataManagement.RestoreBackupPackageAsync(dialog.FileName, _settingsPath);
            await RefreshAsync();
            if (_analysisUsesHistoryScope)
            {
                await QueryHistoryAsync();
            }
            NotifyOperationCompleted("恢复完成", "统计数据已恢复并重新载入。");
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
            MessageBox.Show("请先在分析仪表盘选择有效日期范围。", "InputAtlas");
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
            await SaveNowAsync(false);
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
            NotifyOperationCompleted("删除完成", $"已删除 {affected:N0} 个统计桶，安全备份已创建。");
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
        _capture.InputCounted -= CaptureInputCounted;
        _capture.InputStateChanged -= CaptureInputStateChanged;
        _coordinator.SnapshotChanged -= CoordinatorSnapshotChanged;
        _refreshCancellation?.Cancel();
        _refreshCancellation?.Dispose();
        _refreshGate.Dispose();
        await Task.CompletedTask;
    }

    private void NotifyOperationCompleted(string title, string message)
    {
        _log.Information("operation_completed_notification", $"title={title} message={message}");
        OperationCompleted?.Invoke(title, message);
    }

    private async Task RefreshTrendAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var selectedStart = _analysisRangeStartUtc;
        var selectedEnd = _analysisRangeEndUtc;
        var hasHistoryScope = _analysisUsesHistoryScope &&
                              selectedStart is not null &&
                              selectedEnd is not null &&
                              selectedEnd > selectedStart;
        var start = hasHistoryScope ? selectedStart!.Value : now.AddDays(-7).ToUnixTimeSeconds();
        var end = hasHistoryScope ? selectedEnd!.Value : now.ToUnixTimeSeconds() + 1;
        var granularity = TimeBuckets.ChooseGranularity(TimeSpan.FromSeconds(end - start));
        var queryService = new StatisticsQueryService(_repository);
        var keyboardTask = queryService.QueryAsync(
            new StatisticsQuery(start, end, granularity, InputCategory.Keyboard),
            cancellationToken).AsTask();
        var mouseTask = queryService.QueryAsync(
            new StatisticsQuery(start, end, granularity, InputCategory.MouseButton),
            cancellationToken).AsTask();
        var wheelTask = queryService.QueryAsync(
            new StatisticsQuery(start, end, granularity, InputCategory.Wheel),
            cancellationToken).AsTask();
        await Task.WhenAll(keyboardTask, mouseTask, wheelTask).ConfigureAwait(true);
        var keyboard = await keyboardTask.ConfigureAwait(true);
        var mouse = await mouseTask.ConfigureAwait(true);
        var wheel = await wheelTask.ConfigureAwait(true);
        var activity = BuildActivityPoints(keyboard, mouse, wheel);
        var (displayTimeZone, fallback) = GetEffectiveTimeZone();
        ChartAdapter.UpdateCountsChart(TrendPlot, keyboard, displayTimeZone);
        ChartAdapter.UpdateActivityChart(ActivityPlot, activity, displayTimeZone);
        var displayOffset = displayTimeZone.GetUtcOffset(now.UtcDateTime);
        _log.Debug(
            "analysis_charts_updated",
            $"start_utc={start} end_utc={end} granularity={granularity} " +
            $"keyboard_points={keyboard.Count} activity_points={activity.Length} " +
            $"timezone_id={displayTimeZone.Id} offset_minutes={displayOffset.TotalMinutes:F0} fallback={fallback}");
    }

    private static StatisticsPoint[] BuildActivityPoints(
        IReadOnlyList<StatisticsPoint> keyboard,
        IReadOnlyList<StatisticsPoint> mouse,
        IReadOnlyList<StatisticsPoint> wheel)
    {
        var points = new SortedDictionary<long, ActivityAggregate>();
        foreach (var point in keyboard)
        {
            GetActivityAggregate(points, point).Keyboard = point.Count;
        }

        foreach (var point in mouse)
        {
            GetActivityAggregate(points, point).Mouse = point.Count;
        }

        foreach (var point in wheel)
        {
            GetActivityAggregate(points, point).Wheel = point.Count;
        }

        return points.Select(pair => new StatisticsPoint(
            pair.Key,
            pair.Value.EndUtc,
            ActivityUnits(pair.Value.Keyboard, pair.Value.Mouse, pair.Value.Wheel),
            pair.Value.CoverageSeconds,
            pair.Value.Coverage)).ToArray();
    }

    private static ActivityAggregate GetActivityAggregate(
        SortedDictionary<long, ActivityAggregate> points,
        StatisticsPoint point)
    {
        if (!points.TryGetValue(point.StartUtc, out var aggregate))
        {
            aggregate = new ActivityAggregate(point.EndUtc, point.CoverageSeconds, point.Coverage);
            points[point.StartUtc] = aggregate;
        }
        else
        {
            aggregate.EndUtc = Math.Max(aggregate.EndUtc, point.EndUtc);
            aggregate.CoverageSeconds = Math.Max(aggregate.CoverageSeconds, point.CoverageSeconds);
            if (point.Coverage > aggregate.Coverage)
            {
                aggregate.Coverage = point.Coverage;
            }
        }

        return aggregate;
    }

    private static long ActivityUnits(long keyboard, long mouse, long wheel)
    {
        var keyboardAndMouse = SaturatingSum([keyboard, mouse]);
        var weighted = keyboardAndMouse > long.MaxValue / 10
            ? long.MaxValue
            : keyboardAndMouse * 10;
        return weighted > long.MaxValue - wheel ? long.MaxValue : weighted + wheel;
    }

    private sealed class ActivityAggregate(long endUtc, int coverageSeconds, CoverageState coverage)
    {
        public long EndUtc { get; set; } = endUtc;

        public int CoverageSeconds { get; set; } = coverageSeconds;

        public CoverageState Coverage { get; set; } = coverage;

        public long Keyboard { get; set; }

        public long Mouse { get; set; }

        public long Wheel { get; set; }
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
            _ = dispatcher.InvokeAsync(() => ApplyCaptureStatus(status));
        }
    }

    private void ApplyCaptureStatus(CaptureStatus status)
    {
        _captureStatus = status;
        StatusText = StatusToText(status);
        RaisePropertyChanged(nameof(IsCapturePaused));
        RaisePropertyChanged(nameof(IsCaptureRecording));
        RaisePropertyChanged(nameof(CanToggleCapture));
        RaisePropertyChanged(nameof(CaptureActionText));
        RaisePropertyChanged(nameof(CaptureControlHint));
    }

    private void CaptureInputCounted(InputId input)
    {
        Interlocked.Exchange(ref _liveCountDirty, 1);
        Interlocked.Increment(ref _liveEventsSinceLog);
        ScheduleLiveFrame();
    }

    private void CaptureInputStateChanged(InputId input, bool isPressed)
    {
        _pendingVisualTransitions.Enqueue((input, isPressed));
        ScheduleLiveFrame();
    }

    private void ScheduleLiveFrame()
    {
        if (Interlocked.Exchange(ref _liveFrameScheduled, 1) != 0)
        {
            return;
        }

        if (Application.Current?.Dispatcher is not { } dispatcher)
        {
            Interlocked.Exchange(ref _liveFrameScheduled, 0);
            return;
        }

        _ = dispatcher.InvokeAsync(ProcessLiveFrame, DispatcherPriority.Render);
    }

    private void ProcessLiveFrame()
    {
        var started = Stopwatch.GetTimestamp();
        var mergedStates = 0;
        while (_pendingVisualTransitions.TryDequeue(out var transition))
        {
            mergedStates++;
            InputVisualStateChanged?.Invoke(transition.Input, transition.IsPressed);
        }

        if (Interlocked.Exchange(ref _liveCountDirty, 0) != 0)
        {
            UpdateLivePresentation();
        }

        Interlocked.Exchange(ref _liveFrameScheduled, 0);
        if (!_pendingVisualTransitions.IsEmpty || Volatile.Read(ref _liveCountDirty) != 0)
        {
            ScheduleLiveFrame();
        }

        LogLiveFrameDiagnostics(started, mergedStates);
    }

    private void UpdateLivePresentation()
    {
        var active = _capture.GetCurrentSnapshot();
        if (active.BucketStartUtc < _liveDayStartUtc || active.BucketStartUtc >= _liveDayEndUtc)
        {
            return;
        }

        var counts = new SortedDictionary<InputId, long>();
        foreach (var pair in _todayHistoricalCounts)
        {
            counts[pair.Key] = pair.Value;
        }

        foreach (var pair in active.Counts)
        {
            counts.TryGetValue(pair.Key, out var historical);
            counts[pair.Key] = historical > long.MaxValue - pair.Value
                ? long.MaxValue
                : historical + pair.Value;
        }

        var metrics = MetricsCalculator.Calculate(
            [new BucketSnapshot(active.BucketStartUtc, active.CoverageSeconds, counts, active.UpdatedUtc)]);
        ApplyLiveMetrics(metrics, counts);
        UpdateSelectedDetail();
    }

    private void ApplyLiveMetrics(InputMetrics metrics, SortedDictionary<InputId, long> counts)
    {
        KeyboardCount = metrics.KeyboardCount.ToString("N0", CultureInfo.CurrentCulture);
        MouseCount = metrics.MouseButtonCount.ToString("N0", CultureInfo.CurrentCulture);
        WheelCount = metrics.WheelSteps.ToString("N0", CultureInfo.CurrentCulture);
        _activityScoreValue = metrics.ActivityScore;
        ActivityScore = metrics.ActivityScore.ToString("N1", CultureInfo.CurrentCulture);
        UpdateActivityScoreHeat();
        InputCounts = new ReadOnlyDictionary<InputId, long>(counts);
        if (!_analysisUsesHistoryScope)
        {
            _analysisCounts = new ReadOnlyDictionary<InputId, long>(new Dictionary<InputId, long>(counts));
            AnalysisScopeLabel = "今天（实时）";
            HistoryKeyboardCount = metrics.KeyboardCount.ToString("N0", CultureInfo.CurrentCulture);
            HistoryMouseCount = metrics.MouseButtonCount.ToString("N0", CultureInfo.CurrentCulture);
            HistoryWheelCount = metrics.WheelSteps.ToString("N0", CultureInfo.CurrentCulture);
            HistoryActivityScore = metrics.ActivityScore.ToString("N1", CultureInfo.CurrentCulture);
            HistoryCoverage = "实时覆盖";
            HistorySummary = $"键盘 {metrics.KeyboardCount:N0} · 鼠标 {metrics.MouseButtonCount:N0} · 滚轮 {metrics.WheelSteps:N0} · 实时更新";
            UpdateStatisticsDimensions(_analysisCounts);
        }
    }

    private void UpdateStatisticsDimensions(IReadOnlyDictionary<InputId, long> counts)
    {
        var keyboard = counts
            .Where(pair => pair.Value > 0 && MetricsCalculator.GetCategory(pair.Key) == InputCategory.Keyboard)
            .Select(pair => new
            {
                pair.Key,
                pair.Value,
                Label = KeyboardLayout.Keys.FirstOrDefault(key => key.Input == pair.Key)?.Label ?? InputDisplayName(pair.Key),
            })
            .OrderByDescending(pair => pair.Value)
            .ThenBy(pair => pair.Label, StringComparer.CurrentCulture)
            .ToArray();
        var keyboardTotal = SaturatingSum(keyboard.Select(static pair => pair.Value));
        var leaderboard = keyboard
            .Take(10)
            .Select((pair, index) => new InputRankingItem(
                index + 1,
                pair.Label,
                pair.Value,
                ShareOf(pair.Value, keyboardTotal),
                "键盘"))
            .ToArray();
        InputLeaderboard = leaderboard;

        var pieItems = keyboard
            .Take(8)
            .Select((pair, index) => new InputRankingItem(
                index + 1,
                pair.Label,
                pair.Value,
                ShareOf(pair.Value, keyboardTotal),
                "键盘"))
            .ToList();
        var topTotal = SaturatingSum(pieItems.Select(static item => item.Count));
        var remainder = keyboardTotal >= topTotal ? keyboardTotal - topTotal : 0;
        if (remainder > 0)
        {
            pieItems.Add(new InputRankingItem(
                pieItems.Count + 1,
                "其他按键",
                remainder,
                ShareOf(remainder, keyboardTotal),
                "键盘"));
        }

        var categories = counts
            .Where(static pair => pair.Value > 0)
            .GroupBy(pair => MetricsCalculator.GetCategory(pair.Key))
            .Select(group => new CategorySummaryItem(
                CategoryDisplayName(group.Key),
                SaturatingSum(group.Select(static pair => pair.Value)),
                ShareOf(SaturatingSum(group.Select(static pair => pair.Value)), SaturatingSum(counts.Values)),
                group.Key))
            .OrderByDescending(item => item.Count)
            .ToArray();
        CategorySummaries = categories;
        ChartAdapter.UpdateKeyDistributionChart(KeyDistributionPlot, pieItems);
        ChartAdapter.UpdateCategoryDistributionChart(CategoryDistributionPlot, categories);
    }

    private static double ShareOf(long value, long total) =>
        total <= 0 ? 0 : Math.Clamp(value / (double)total, 0, 1);

    private static long SaturatingSum(IEnumerable<long> values)
    {
        var total = 0L;
        foreach (var value in values)
        {
            total = total > long.MaxValue - value ? long.MaxValue : total + value;
        }

        return total;
    }

    private void UpdateActivityScoreHeat()
    {
        var heatCount = _activityScoreValue >= long.MaxValue
            ? long.MaxValue
            : (long)Math.Max(0m, decimal.Truncate(_activityScoreValue));
        var color = HeatmapPalette.GetColor(heatCount, Color.FromRgb(255, 252, 246), ScoreHeatmapThresholds);
        ActivityScoreBackground = new SolidColorBrush(color);
        ActivityScoreForeground = new SolidColorBrush(Color.FromRgb(29, 27, 32));
    }

    private static bool TryParsePositiveLong(string text, out long value) =>
        (long.TryParse(text, NumberStyles.Integer | NumberStyles.AllowThousands, CultureInfo.CurrentCulture, out value) ||
         long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value)) &&
        value > 0;

    private void LogLiveFrameDiagnostics(long started, int mergedStates)
    {
        if (_settings.DiagnosticUntilUtc is not { } diagnosticUntil || diagnosticUntil <= DateTimeOffset.UtcNow)
        {
            return;
        }

        if (Stopwatch.GetElapsedTime(_lastLiveFrameLogTimestamp) < TimeSpan.FromSeconds(5))
        {
            return;
        }

        var events = Interlocked.Exchange(ref _liveEventsSinceLog, 0);
        _lastLiveFrameLogTimestamp = Stopwatch.GetTimestamp();
        _log.Debug(
            "live_ui_frame",
            $"counted_events={events} merged_states={mergedStates} duration_ms={Stopwatch.GetElapsedTime(started).TotalMilliseconds:F2}");
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

    private static string CategoryDisplayName(InputCategory category) => category switch
    {
        InputCategory.Keyboard => "键盘按键",
        InputCategory.MouseButton => "鼠标按键",
        InputCategory.Wheel => "滚轮",
        InputCategory.Other => "其他输入",
        _ => "其他输入",
    };

    private static string InputDisplayName(InputId input) => input.Value switch
    {
        900 => "其他键盘",
        901 => "不可观测键",
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
