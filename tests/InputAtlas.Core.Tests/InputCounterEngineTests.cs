using InputAtlas.Core;

namespace InputAtlas.Core.Tests;

public sealed class InputCounterEngineTests
{
    private const long Now = 1_800_000_000;

    [Fact]
    public void RepeatedDownBeforeUpCountsOnce()
    {
        var engine = new InputCounterEngine(Now);
        var down = new RawKeyboardSample(0x11, 0x57, false, false, false);
        var up = down with { IsBreak = true };

        engine.HandleKeyboard(down, Now);
        engine.HandleKeyboard(down, Now);
        engine.HandleKeyboard(down, Now);
        engine.HandleKeyboard(up, Now);

        Assert.Equal(1, engine.Snapshot(Now).Counts[new InputId(0x1A)]);
    }

    [Fact]
    public void DownUpDownUpCountsTwice()
    {
        var engine = new InputCounterEngine(Now);
        var down = new RawKeyboardSample(0x2E, 0x43, false, false, false);
        var up = down with { IsBreak = true };

        engine.HandleKeyboard(down, Now);
        engine.HandleKeyboard(up, Now);
        engine.HandleKeyboard(down, Now);
        engine.HandleKeyboard(up, Now);

        Assert.Equal(2, engine.Snapshot(Now).Counts[new InputId(0x06)]);
    }

    [Fact]
    public void MainAndNumpadEnterAreSeparate()
    {
        var engine = new InputCounterEngine(Now);
        engine.HandleKeyboard(new RawKeyboardSample(0x1C, 0x0D, false, false, false), Now);
        engine.HandleKeyboard(new RawKeyboardSample(0x1C, 0x0D, true, false, false), Now);

        var counts = engine.Snapshot(Now).Counts;
        Assert.Equal(1, counts[new InputId(0x28)]);
        Assert.Equal(1, counts[new InputId(0x58)]);
    }

    [Fact]
    public void OppositeWheelRemaindersDoNotCancel()
    {
        var engine = new InputCounterEngine(Now);
        engine.HandleMouse(new RawMouseSample(RawMouseButtons.VerticalWheel, 60), Now);
        engine.HandleMouse(new RawMouseSample(RawMouseButtons.VerticalWheel, -60), Now);
        Assert.Empty(engine.Snapshot(Now).Counts);

        engine.HandleMouse(new RawMouseSample(RawMouseButtons.VerticalWheel, 60), Now);
        engine.HandleMouse(new RawMouseSample(RawMouseButtons.VerticalWheel, -60), Now);

        var counts = engine.Snapshot(Now).Counts;
        Assert.Equal(1, counts[InputId.WheelUp]);
        Assert.Equal(1, counts[InputId.WheelDown]);
    }

    [Fact]
    public void MouseDownRequiresReleaseBeforeSecondCount()
    {
        var engine = new InputCounterEngine(Now);
        engine.HandleMouse(new RawMouseSample(RawMouseButtons.LeftDown, 0), Now);
        engine.HandleMouse(new RawMouseSample(RawMouseButtons.LeftDown, 0), Now);
        engine.HandleMouse(new RawMouseSample(RawMouseButtons.LeftUp, 0), Now);
        engine.HandleMouse(new RawMouseSample(RawMouseButtons.LeftDown, 0), Now);

        Assert.Equal(2, engine.Snapshot(Now).Counts[InputId.MouseLeft]);
    }

    [Fact]
    public void ResetPressedRecoversFromMissingKeyUp()
    {
        var engine = new InputCounterEngine(Now);
        var down = new RawKeyboardSample(0x1E, 0x41, false, false, false);
        engine.HandleKeyboard(down, Now);
        engine.ResetPressedStates();
        engine.HandleKeyboard(down, Now);

        Assert.Equal(2, engine.Snapshot(Now).Counts[new InputId(0x04)]);
    }
}

