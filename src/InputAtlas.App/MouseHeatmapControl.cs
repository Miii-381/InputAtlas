using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using InputAtlas.Core;

namespace InputAtlas.App;

public sealed class MouseHeatmapControl : FrameworkElement
{
    private const double ViewBoxWidth = 320;
    private const double ViewBoxHeight = 280;
    private static readonly Color OutlineColor = Color.FromRgb(220, 209, 193);
    private static readonly Color IdleSurfaceColor = Color.FromRgb(255, 252, 246);

    public static readonly DependencyProperty CountsProperty = DependencyProperty.Register(
        nameof(Counts),
        typeof(IReadOnlyDictionary<InputId, long>),
        typeof(MouseHeatmapControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ThresholdsProperty = DependencyProperty.Register(
        nameof(Thresholds),
        typeof(HeatmapThresholds),
        typeof(MouseHeatmapControl),
        new FrameworkPropertyMetadata(HeatmapThresholds.Default, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ThresholdModeProperty = DependencyProperty.Register(
        nameof(ThresholdMode),
        typeof(HeatmapThresholdMode),
        typeof(MouseHeatmapControl),
        new FrameworkPropertyMetadata(HeatmapThresholdMode.FixedCount, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty AccentColorProperty = DependencyProperty.Register(
        nameof(AccentColor),
        typeof(Color),
        typeof(MouseHeatmapControl),
        new FrameworkPropertyMetadata(Color.FromRgb(127, 162, 162), FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FontFamilyProperty = DependencyProperty.Register(
        nameof(FontFamily),
        typeof(System.Windows.Media.FontFamily),
        typeof(MouseHeatmapControl),
        new FrameworkPropertyMetadata(
            new System.Windows.Media.FontFamily("Segoe UI Variable Text, Segoe UI, Microsoft YaHei UI"),
            FrameworkPropertyMetadataOptions.AffectsRender));

    private readonly Dictionary<InputId, AnimationState> _animations = new();
    private long _lastFrameTimestamp;
    private bool _renderingSubscribed;
    private HeatmapThresholds _renderThresholds = HeatmapThresholds.Default;

    public MouseHeatmapControl()
    {
        Unloaded += (_, _) => ResetAnimations();
    }

    public IReadOnlyDictionary<InputId, long>? Counts
    {
        get => (IReadOnlyDictionary<InputId, long>?)GetValue(CountsProperty);
        set => SetValue(CountsProperty, value);
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

    public void SetInputState(InputId input, bool isPressed)
    {
        if (!IsMouseVisualInput(input))
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
        StopRendering();
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        if (RenderSize.Width <= 0 || RenderSize.Height <= 0)
        {
            return;
        }

        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        _renderThresholds = HeatmapPalette.ResolveThresholds(
            [
                GetCount(InputId.MouseLeft),
                GetCount(InputId.MouseRight),
                GetCount(InputId.MouseMiddle),
                GetCount(InputId.MouseBack),
                GetCount(InputId.MouseForward),
                GetCount(InputId.WheelUp),
                GetCount(InputId.WheelDown),
                GetCount(InputId.WheelLeft),
                GetCount(InputId.WheelRight),
            ],
            Thresholds,
            ThresholdMode);
        var scale = Math.Max(0.01, Math.Min(RenderSize.Width / ViewBoxWidth, RenderSize.Height / ViewBoxHeight));
        var offsetX = (RenderSize.Width - ViewBoxWidth * scale) / 2 + 60 * scale;
        var offsetY = (RenderSize.Height - ViewBoxHeight * scale) / 2 + 20 * scale;
        drawingContext.PushTransform(new MatrixTransform(scale, 0, 0, scale, offsetX, offsetY));
        DrawAmbientElevation(drawingContext);
        DrawMouseBody(drawingContext);
        DrawPrimaryButtons(drawingContext, dpi);
        DrawWheel(drawingContext, dpi);
        DrawScrollIndicators(drawingContext);
        DrawSideButtons(drawingContext, dpi);
        DrawExternalStatistics(drawingContext, dpi);
        drawingContext.Pop();
    }

    private static void DrawMouseBody(DrawingContext context)
    {
        var body = CreateBodyGeometry();
        var bodyPen = CreateOutlinePen(3);
        context.DrawGeometry(new SolidColorBrush(IdleSurfaceColor), bodyPen, body);
    }

    private void DrawPrimaryButtons(DrawingContext context, double dpi)
    {
        DrawPrimaryButton(
            context,
            CreateLeftButtonGeometry(),
            new Rect(43, 46, 52, 58),
            InputId.MouseLeft,
            "左键",
            dpi);
        DrawPrimaryButton(
            context,
            CreateRightButtonGeometry(),
            new Rect(105, 46, 52, 58),
            InputId.MouseRight,
            "右键",
            dpi);
    }

    private void DrawPrimaryButton(
        DrawingContext context,
        StreamGeometry geometry,
        Rect textBounds,
        InputId input,
        string label,
        double dpi)
    {
        var count = GetCount(input);
        var progress = GetAnimationProgress(input);
        var color = PressedColor(HeatmapPalette.GetColor(count, IdleSurfaceColor, _renderThresholds, ThresholdMode), progress);
        context.PushTransform(new TranslateTransform(0, progress * 1.8));
        context.DrawGeometry(new SolidColorBrush(color), CreateInteractionPen(progress), geometry);
        DrawStateLayer(context, geometry, progress);
        DrawPrimaryButtonText(context, textBounds, label, color, dpi);
        context.Pop();
    }

    private void DrawWheel(DrawingContext context, double dpi)
    {
        var count = GetCount(InputId.MouseMiddle);
        var progress = GetAnimationProgress(InputId.MouseMiddle);
        var color = PressedColor(HeatmapPalette.GetColor(count, IdleSurfaceColor, _renderThresholds, ThresholdMode), progress);
        var wheel = new Rect(92, 30, 16, 50);
        context.PushTransform(new TranslateTransform(0, progress * 1.8));
        context.DrawRoundedRectangle(
            new SolidColorBrush(color),
            CreateInteractionPen(progress),
            wheel,
            8,
            8);
        if (progress > 0)
        {
            context.DrawRoundedRectangle(
                new SolidColorBrush(Color.FromArgb((byte)(54 * progress), 255, 255, 255)),
                null,
                wheel,
                8,
                8);
        }

        context.Pop();
    }

    private void DrawScrollIndicators(DrawingContext context)
    {
        var upCount = GetCount(InputId.WheelUp);
        var downCount = GetCount(InputId.WheelDown);
        DrawChevron(
            context,
            new Point(90, 20),
            new Point(100, 8),
            new Point(110, 20),
            InputId.WheelUp,
            upCount,
            -1);
        DrawChevron(
            context,
            new Point(90, 90),
            new Point(100, 102),
            new Point(110, 90),
            InputId.WheelDown,
            downCount,
            1);
    }

    private void DrawSideButtons(DrawingContext context, double dpi)
    {
        DrawSideButton(
            context,
            CreateSideButtonGeometry(120, 145),
            new Rect(30, 120, 10, 25),
            InputId.MouseBack,
            GetCount(InputId.MouseBack),
            dpi);
        DrawSideButton(
            context,
            CreateSideButtonGeometry(155, 180),
            new Rect(30, 155, 10, 25),
            InputId.MouseForward,
            GetCount(InputId.MouseForward),
            dpi);
    }

    private void DrawSideButton(
        DrawingContext context,
        StreamGeometry geometry,
        Rect textBounds,
        InputId input,
        long count,
        double dpi)
    {
        var progress = GetAnimationProgress(input);
        var color = PressedColor(HeatmapPalette.GetColor(count, IdleSurfaceColor, _renderThresholds, ThresholdMode), progress);
        context.PushTransform(new TranslateTransform(0, progress * 1.5));
        context.DrawGeometry(new SolidColorBrush(color), CreateInteractionPen(progress), geometry);
        DrawStateLayer(context, geometry, progress);
        context.Pop();
    }

    private void DrawPrimaryButtonText(
        DrawingContext context,
        Rect bounds,
        string label,
        Color background,
        double dpi)
    {
        var foreground = TextColorFor(background);
        var text = new FormattedText(
            label,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            new Typeface(FontFamily, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
            10.5,
            new SolidColorBrush(foreground),
            dpi)
        {
            TextAlignment = TextAlignment.Center,
            MaxTextWidth = bounds.Width,
        };
        context.DrawText(text, new Point(bounds.X, bounds.Y + Math.Max(0, (bounds.Height - text.Height) / 2)));
    }

    private void DrawExternalStatistics(DrawingContext context, double dpi)
    {
        DrawLeaderLabel(context, InputId.MouseLeft, "左键", new Point(57, 58), new Point(22, 48), new Point(22, 48), new Rect(-55, 25, 72, 48), true, dpi);
        DrawLeaderLabel(context, InputId.MouseRight, "右键", new Point(143, 58), new Point(179, 48), new Point(179, 48), new Rect(185, 25, 74, 48), false, dpi);
        DrawTopLeaderLabel(context, InputId.MouseMiddle, "中键", new Point(100, 30), new Point(100, -1), new Rect(60, -20, 80, 42), dpi);
        DrawLeaderLabel(context, InputId.MouseBack, "按键4", new Point(34, 132), new Point(8, 132), new Point(8, 132), new Rect(-55, 108, 58, 48), true, dpi);
        DrawLeaderLabel(context, InputId.MouseForward, "按键5", new Point(34, 167), new Point(8, 167), new Point(8, 167), new Rect(-55, 143, 58, 48), true, dpi);
        DrawLeaderLabel(context, InputId.WheelUp, "上滚", new Point(110, 17), new Point(174, 17), new Point(179, 138), new Rect(185, 114, 74, 48), false, dpi);
        DrawLeaderLabel(context, InputId.WheelDown, "下滚", new Point(110, 93), new Point(174, 93), new Point(179, 190), new Rect(185, 166, 74, 48), false, dpi);
    }

    private void DrawLeaderLabel(
        DrawingContext context,
        InputId input,
        string label,
        Point anchor,
        Point bend,
        Point endpoint,
        Rect bounds,
        bool alignRight,
        double dpi)
    {
        var count = GetCount(input);
        var heatColor = HeatmapPalette.GetColor(count, IdleSurfaceColor, _renderThresholds, ThresholdMode);
        var progress = GetAnimationProgress(input);
        var leaderColor = PressedColor(Blend(Color.FromRgb(76, 103, 108), heatColor, 0.18), progress);
        var edge = new Point(alignRight ? bounds.Right : bounds.Left, endpoint.Y);
        var pen = new Pen(new SolidColorBrush(leaderColor), 1.4 + progress * 0.6)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round,
        };
        context.DrawLine(pen, anchor, bend);
        context.DrawLine(pen, bend, endpoint);
        context.DrawLine(pen, endpoint, edge);
        context.DrawEllipse(new SolidColorBrush(leaderColor), null, anchor, 2.8 + progress, 2.8 + progress);

        var text = new FormattedText(
            $"{label}\n{count:N0}",
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            new Typeface(FontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
            10.5,
            new SolidColorBrush(Color.FromRgb(98, 91, 113)),
            dpi)
        {
            TextAlignment = alignRight ? TextAlignment.Right : TextAlignment.Left,
            MaxTextWidth = bounds.Width,
            MaxTextHeight = bounds.Height,
            Trimming = TextTrimming.CharacterEllipsis,
        };
        var countStart = label.Length + 1;
        text.SetFontSize(19, countStart, text.Text.Length - countStart);
        text.SetFontWeight(FontWeights.Bold, countStart, text.Text.Length - countStart);
        text.SetForegroundBrush(new SolidColorBrush(Color.FromRgb(29, 27, 32)), countStart, text.Text.Length - countStart);
        context.DrawText(text, new Point(bounds.X, bounds.Y));
    }

    private void DrawTopLeaderLabel(
        DrawingContext context,
        InputId input,
        string label,
        Point anchor,
        Point endpoint,
        Rect bounds,
        double dpi)
    {
        var count = GetCount(input);
        var heatColor = HeatmapPalette.GetColor(count, IdleSurfaceColor, _renderThresholds, ThresholdMode);
        var progress = GetAnimationProgress(input);
        var leaderColor = PressedColor(Blend(Color.FromRgb(76, 103, 108), heatColor, 0.18), progress);
        var pen = new Pen(new SolidColorBrush(leaderColor), 1.4 + progress * 0.6)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
        };
        context.DrawLine(pen, anchor, endpoint);
        context.DrawEllipse(new SolidColorBrush(leaderColor), null, anchor, 2.8 + progress, 2.8 + progress);

        var text = new FormattedText(
            $"{label}  {count:N0}",
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            new Typeface(FontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
            10.5,
            new SolidColorBrush(Color.FromRgb(98, 91, 113)),
            dpi)
        {
            TextAlignment = TextAlignment.Center,
            MaxTextWidth = bounds.Width,
            MaxTextHeight = bounds.Height,
            Trimming = TextTrimming.CharacterEllipsis,
        };
        var countStart = label.Length + 2;
        text.SetFontSize(19, countStart, text.Text.Length - countStart);
        text.SetFontWeight(FontWeights.Bold, countStart, text.Text.Length - countStart);
        text.SetForegroundBrush(new SolidColorBrush(Color.FromRgb(29, 27, 32)), countStart, text.Text.Length - countStart);
        context.DrawText(text, new Point(bounds.X, bounds.Y));
    }

    private void DrawChevron(
        DrawingContext context,
        Point start,
        Point middle,
        Point end,
        InputId input,
        long count,
        double direction)
    {
        var progress = GetAnimationProgress(input);
        var heatColor = HeatmapPalette.GetColor(count, IdleSurfaceColor, _renderThresholds, ThresholdMode);
        var color = PressedColor(heatColor, progress);
        var pen = new Pen(new SolidColorBrush(color), 3 + progress * 1.8)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round,
        };
        context.PushTransform(new TranslateTransform(0, direction * progress * 3.2));
        if (progress > 0)
        {
            context.DrawEllipse(
                new SolidColorBrush(Color.FromArgb((byte)(36 * progress), color.R, color.G, color.B)),
                null,
                middle,
                18 + progress * 4,
                12 + progress * 3);
        }

        context.DrawLine(pen, start, middle);
        context.DrawLine(pen, middle, end);
        context.Pop();
    }

    private static bool IsMouseVisualInput(InputId input) =>
        input == InputId.MouseLeft ||
        input == InputId.MouseRight ||
        input == InputId.MouseMiddle ||
        input == InputId.MouseBack ||
        input == InputId.MouseForward ||
        input == InputId.WheelUp ||
        input == InputId.WheelDown ||
        input == InputId.WheelLeft ||
        input == InputId.WheelRight;

    private Color PressedColor(Color color, double progress) =>
        progress <= 0
            ? color
            : Blend(color, AccentColor, progress * 0.58);

    private static void DrawStateLayer(DrawingContext context, Geometry geometry, double progress)
    {
        if (progress <= 0)
        {
            return;
        }

        context.DrawGeometry(
            new SolidColorBrush(Color.FromArgb((byte)(48 * progress), 255, 255, 255)),
            null,
            geometry);
    }

    private Pen CreateInteractionPen(double progress) => new(
        new SolidColorBrush(Blend(OutlineColor, AccentColor, progress * 0.82)),
        3 + progress * 1.2)
    {
        LineJoin = PenLineJoin.Round,
        StartLineCap = PenLineCap.Round,
        EndLineCap = PenLineCap.Round,
    };

    private static Pen CreateOutlinePen(double thickness) => new(new SolidColorBrush(OutlineColor), thickness)
    {
        LineJoin = PenLineJoin.Round,
        StartLineCap = PenLineCap.Round,
        EndLineCap = PenLineCap.Round,
    };

    private static StreamGeometry CreateBodyGeometry()
    {
        var geometry = new StreamGeometry();
        using (var drawing = geometry.Open())
        {
            drawing.BeginFigure(new Point(40, 80), true, true);
            drawing.LineTo(new Point(40, 180), true, false);
            drawing.BezierTo(new Point(40, 220), new Point(80, 240), new Point(100, 240), true, false);
            drawing.BezierTo(new Point(120, 240), new Point(160, 220), new Point(160, 180), true, false);
            drawing.LineTo(new Point(160, 80), true, false);
        }

        geometry.Freeze();
        return geometry;
    }

    private static StreamGeometry CreateLeftButtonGeometry()
    {
        var geometry = new StreamGeometry();
        using (var drawing = geometry.Open())
        {
            drawing.BeginFigure(new Point(40, 80), true, true);
            drawing.LineTo(new Point(40, 50), true, false);
            drawing.BezierTo(new Point(40, 20), new Point(60, 10), new Point(98, 10), true, false);
            drawing.LineTo(new Point(98, 110), true, false);
            drawing.LineTo(new Point(40, 110), true, false);
        }

        geometry.Freeze();
        return geometry;
    }

    private static StreamGeometry CreateRightButtonGeometry()
    {
        var geometry = new StreamGeometry();
        using (var drawing = geometry.Open())
        {
            drawing.BeginFigure(new Point(102, 10), true, true);
            drawing.LineTo(new Point(102, 110), true, false);
            drawing.LineTo(new Point(160, 110), true, false);
            drawing.LineTo(new Point(160, 50), true, false);
            drawing.BezierTo(new Point(160, 20), new Point(140, 10), new Point(102, 10), true, false);
        }

        geometry.Freeze();
        return geometry;
    }

    private static StreamGeometry CreateSideButtonGeometry(double top, double bottom)
    {
        var geometry = new StreamGeometry();
        using (var drawing = geometry.Open())
        {
            drawing.BeginFigure(new Point(35, top), true, true);
            drawing.LineTo(new Point(40, top), true, false);
            drawing.LineTo(new Point(40, bottom), true, false);
            drawing.LineTo(new Point(35, bottom), true, false);
            drawing.BezierTo(new Point(30, bottom), new Point(30, top), new Point(35, top), true, false);
        }

        geometry.Freeze();
        return geometry;
    }

    private void DrawAmbientElevation(DrawingContext context)
    {
        context.DrawEllipse(
            new SolidColorBrush(Color.FromArgb(18, AccentColor.R, AccentColor.G, AccentColor.B)),
            null,
            new Point(100, 132),
            92,
            126);
        context.DrawEllipse(
            new SolidColorBrush(Color.FromArgb(11, 29, 27, 32)),
            null,
            new Point(100, 137),
            75,
            116);
    }

    private long GetCount(InputId input)
    {
        long count = 0;
        Counts?.TryGetValue(input, out count);
        return count;
    }

    private double GetAnimationProgress(InputId input) =>
        _animations.TryGetValue(input, out var state) ? state.Progress : 0;

    private static Color TextColorFor(Color background) =>
        RelativeLuminance(background) < 0.50 ? Colors.White : Color.FromRgb(41, 37, 36);

    private static double RelativeLuminance(Color color) =>
        (0.2126 * color.R + 0.7152 * color.G + 0.0722 * color.B) / 255d;

    private static Color Blend(Color start, Color end, double amount)
    {
        amount = Math.Clamp(amount, 0, 1);
        return Color.FromRgb(
            (byte)(start.R + (end.R - start.R) * amount),
            (byte)(start.G + (end.G - start.G) * amount),
            (byte)(start.B + (end.B - start.B) * amount));
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

            if (state.Target == 0 && state.Progress == 0)
            {
                _animations.Remove(pair.Key);
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
