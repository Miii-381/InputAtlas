using System.Windows.Media.Imaging;

namespace InputAtlas.App.Pet;

public sealed class PetFrameSet
{
    private const string Root = "pack://application:,,,/InputAtlas;component/Assets/Pet/GoldenMaid/";
    private readonly IReadOnlyDictionary<PetVisualState, BitmapSource> _frames;

    private PetFrameSet(IReadOnlyDictionary<PetVisualState, BitmapSource> frames)
    {
        _frames = frames;
    }

    public BitmapSource this[PetVisualState state] => _frames[state];

    public static PetFrameSet Load()
    {
        var frames = new Dictionary<PetVisualState, BitmapSource>
        {
            [PetVisualState.Idle] = LoadFrame("idle-01.png"),
            [PetVisualState.KeyboardLeft] = LoadFrame("keyboard-left-01.png"),
            [PetVisualState.KeyboardRight] = LoadFrame("keyboard-right-01.png"),
            [PetVisualState.KeyboardBoth] = LoadFrame("keyboard-both-01.png"),
            [PetVisualState.MouseLeft] = LoadFrame("mouse-left-01.png"),
            [PetVisualState.MouseRight] = LoadFrame("mouse-right-01.png"),
            [PetVisualState.MouseMiddle] = LoadFrame("mouse-middle-01.png"),
            [PetVisualState.MouseBack] = LoadFrame("mouse-back-01.png"),
            [PetVisualState.MouseForward] = LoadFrame("mouse-forward-01.png"),
            [PetVisualState.Paused] = LoadFrame("paused-01.png"),
        };
        return new PetFrameSet(frames);
    }

    private static BitmapImage LoadFrame(string fileName)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
        image.UriSource = new Uri(Root + fileName, UriKind.Absolute);
        image.EndInit();
        image.Freeze();
        return image;
    }
}
