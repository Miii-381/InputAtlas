using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using InputAtlas.Core;

namespace InputAtlas.App;

public sealed class KeyboardHeatmapControl : FrameworkElement
{
    public static readonly DependencyProperty LayoutProperty = DependencyProperty.Register(
        nameof(Layout),
        typeof(KeyboardLayoutDefinition),
        typeof(KeyboardHeatmapControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty CountsProperty = DependencyProperty.Register(
        nameof(Counts),
        typeof(IReadOnlyDictionary<InputId, long>),
        typeof(KeyboardHeatmapControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SelectedInputProperty = DependencyProperty.Register(
        nameof(SelectedInput),
        typeof(InputId?),
        typeof(KeyboardHeatmapControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ThresholdsProperty = DependencyProperty.Register(
        nameof(Thresholds),
        typeof(HeatmapThresholds),
        typeof(KeyboardHeatmapControl),
        new FrameworkPropertyMetadata(HeatmapThresholds.Default, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ThresholdModeProperty = DependencyProperty.Register(
        nameof(ThresholdMode),
        typeof(HeatmapThresholdMode),
        typeof(KeyboardHeatmapControl),
        new FrameworkPropertyMetadata(HeatmapThresholdMode.FixedCount, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty AccentColorProperty = DependencyProperty.Register(
        nameof(AccentColor),
        typeof(Color),
        typeof(KeyboardHeatmapControl),
        new FrameworkPropertyMetadata(Color.FromRgb(127, 162, 162), FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FontFamilyProperty = DependencyProperty.Register(
        nameof(FontFamily),
        typeof(System.Windows.Media.FontFamily),
        typeof(KeyboardHeatmapControl),
        new FrameworkPropertyMetadata(
            new System.Windows.Media.FontFamily("Segoe UI Variable Text, Segoe UI, Microsoft YaHei UI"),
            FrameworkPropertyMetadataOptions.AffectsRender));

    private readonly KeyboardRenderer2D _renderer = new();
    private readonly Dictionary<InputId, AnimationState> _animations = new();
    private readonly Dictionary<InputId, double> _animationProgress = new();
    private long _lastFrameTimestamp;
    private bool _renderingSubscribed;

    public KeyboardHeatmapControl()
    {
        Unloaded += (_, _) => ResetAnimations();
    }

    public KeyboardLayoutDefinition? Layout
    {
        get => (KeyboardLayoutDefinition?)GetValue(LayoutProperty);
        set => SetValue(LayoutProperty, value);
    }

    public IReadOnlyDictionary<InputId, long>? Counts
    {
        get => (IReadOnlyDictionary<InputId, long>?)GetValue(CountsProperty);
        set => SetValue(CountsProperty, value);
    }

    public InputId? SelectedInput
    {
        get => (InputId?)GetValue(SelectedInputProperty);
        set => SetValue(SelectedInputProperty, value);
    }

    public HeatmapThresholds Thresholds
    {
        get => (HeatmapThresholds)GetValue(ThresholdsProperty);
        set => SetValue(ThresholdsProperty, value);
    }

    public HeatmapThresholdMode ThresholdMode
    {
        get => (HeatmapThresholdMode)GetValue(ThresholdModeProperty);
        set => SetValue(ThresholdModeProperty, value);
    }

    public Color AccentColor
    {
        get => (Color)GetValue(AccentColorProperty);
        set => SetValue(AccentColorProperty, value);
    }

    public System.Windows.Media.FontFamily FontFamily
    {
        get => (System.Windows.Media.FontFamily)GetValue(FontFamilyProperty);
        set => SetValue(FontFamilyProperty, value);
    }

    public event Action<InputId>? InputSelected;

    public InputId? HitTestInput(Point point) =>
        Layout is null ? null : _renderer.HitTest(point, RenderSize, Layout);

    public void SetInputState(InputId input, bool isPressed)
    {
        if (input.Value >= InputId.MouseLeft.Value)
        {
            return;
        }

        if (!_animations.TryGetValue(input, out var state))
        {
            state = new AnimationState();
            _animations[input] = state;
        }

        if (isPressed)
        {
            state.Target = 1d;
            state.ReleasePending = false;
        }
        else if (state.Target > 0 && state.Progress < 0.72)
        {
            state.Target = 1d;
            state.ReleasePending = true;
        }
        else
        {
            state.Target = 0d;
            state.ReleasePending = false;
        }

        EnsureRendering();
    }

    public void ResetAnimations()
    {
        _animations.Clear();
        _animationProgress.Clear();
        StopRendering();
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        if (Layout is null)
        {
            return;
        }

        var dpi = VisualTreeHelper.GetDpi(this);
        var counts = Counts ?? new Dictionary<InputId, long>();
        var effectiveThresholds = HeatmapPalette.ResolveThresholds(
            Layout.Keys.Select(key => counts.TryGetValue(key.Input, out var count) ? count : 0),
            Thresholds,
            ThresholdMode);
        _renderer.Render(
            drawingContext,
            RenderSize,
            Layout,
            counts,
            SelectedInput,
            effectiveThresholds,
            ThresholdMode,
            _animationProgress,
            AccentColor,
            FontFamily,
            dpi.PixelsPerDip);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (Layout is null)
        {
            return;
        }

        var input = HitTestInput(e.GetPosition(this));
        if (input is { } selected)
        {
            InputSelected?.Invoke(selected);
            InvalidateVisual();
        }
    }

    private void EnsureRendering()
    {
        if (_renderingSubscribed)
        {
            return;
        }

        _lastFrameTimestamp = Stopwatch.GetTimestamp();
        CompositionTarget.Rendering += RenderAnimationFrame;
        _renderingSubscribed = true;
    }

    private void StopRendering()
    {
        if (!_renderingSubscribed)
        {
            return;
        }

        CompositionTarget.Rendering -= RenderAnimationFrame;
        _renderingSubscribed = false;
    }

    private void RenderAnimationFrame(object? sender, EventArgs e)
    {
        var now = Stopwatch.GetTimestamp();
        var elapsed = Math.Min(0.05, Stopwatch.GetElapsedTime(_lastFrameTimestamp, now).TotalSeconds);
        _lastFrameTimestamp = now;
        foreach (var pair in _animations.ToArray())
        {
            var state = pair.Value;
            var duration = state.Target > state.Progress ? 0.10 : 0.15;
            var step = elapsed / duration;
            state.Progress = state.Target > state.Progress
                ? Math.Min(state.Target, state.Progress + step)
                : Math.Max(state.Target, state.Progress - step);
            if (state.ReleasePending && state.Progress >= 0.72)
            {
                state.Target = 0d;
                state.ReleasePending = false;
            }

            _animationProgress[pair.Key] = state.Progress;
            if (state.Target == 0 && state.Progress == 0)
            {
                _animations.Remove(pair.Key);
                _animationProgress.Remove(pair.Key);
            }
        }

        InvalidateVisual();
        if (_animations.Count == 0)
        {
            StopRendering();
        }
    }

    private sealed class AnimationState
    {
        public double Progress { get; set; }

        public double Target { get; set; }

        public bool ReleasePending { get; set; }
    }
}
