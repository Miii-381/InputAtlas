using InputAtlas.App;
using InputAtlas.Core;
using InputAtlas.Storage;

namespace InputAtlas.App.Tests;

public sealed class LayoutTests
{
    [Fact]
    public void AnsiLayoutContainsExactlyOneHundredAndFourUniqueKeys()
    {
        var layout = KeyboardLayoutLoader.Load(KeyboardLayoutKind.Ansi104);

        Assert.Equal(104, layout.Keys.Count);
        Assert.Equal(104, layout.Keys.Select(static key => key.Input).Distinct().Count());
        Assert.All(layout.Keys, key => AssertInsideLayout(layout, key));
    }

    [Fact]
    public void CompactLayoutMatchesFrozenSixRowsAndFnIsUnavailable()
    {
        var layout = KeyboardLayoutLoader.Load(KeyboardLayoutKind.Compact75);

        Assert.Equal(81, layout.Keys.Count);
        Assert.Equal(6, layout.Keys.Select(static key => key.Y).Distinct().Count());
        var functionKey = Assert.Single(layout.Keys, static key => key.Input == InputId.UnobservableFn);
        Assert.False(functionKey.Observable);
        Assert.All(layout.Keys, key => AssertInsideLayout(layout, key));
    }

    private static void AssertInsideLayout(KeyboardLayoutDefinition layout, KeyDefinition key)
    {
        Assert.True(key.X >= 0 && key.Y >= 0);
        Assert.True(key.Width > 0 && key.Height > 0);
        Assert.True(key.X + key.Width <= layout.WidthUnits);
        Assert.True(key.Y + key.Height <= layout.HeightUnits);
    }
}

