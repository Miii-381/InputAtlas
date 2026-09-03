using InputAtlas.App.Pet;

namespace InputAtlas.App.Tests;

public sealed class PetFrameSetTests
{
    [Fact]
    public void AllPackagedFramesLoadAtExpectedDimensions()
    {
        var frames = PetFrameSet.Load();

        foreach (var state in Enum.GetValues<PetVisualState>())
        {
            Assert.Equal(1403, frames[state].PixelWidth);
            Assert.Equal(1123, frames[state].PixelHeight);
            Assert.True(frames[state].IsFrozen);
        }
    }
}
