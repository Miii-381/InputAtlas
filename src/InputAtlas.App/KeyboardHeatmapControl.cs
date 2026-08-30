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

    private readonly KeyboardRenderer2D _renderer = new();

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

    public event Action<InputId>? InputSelected;

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        if (Layout is null)
        {
            return;
        }

        var dpi = VisualTreeHelper.GetDpi(this);
        _renderer.Render(
            drawingContext,
            RenderSize,
            Layout,
            Counts ?? new Dictionary<InputId, long>(),
            SelectedInput,
            dpi.PixelsPerDip);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (Layout is null)
        {
            return;
        }

        var input = _renderer.HitTest(e.GetPosition(this), RenderSize, Layout);
        if (input is { } selected)
        {
            SelectedInput = selected;
            InputSelected?.Invoke(selected);
            InvalidateVisual();
        }
    }
}
