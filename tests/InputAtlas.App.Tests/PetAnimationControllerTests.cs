using InputAtlas.App.Pet;
using InputAtlas.Core;

namespace InputAtlas.App.Tests;

public sealed class PetAnimationControllerTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 2, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void SimultaneousKeyboardStateUsesCurrentPressedSets()
    {
        var controller = new PetAnimationController(Start);
        var left = new InputId(0x04);
        var right = new InputId(0x0B);

        controller.ApplyInput(left, true, Start);
        Assert.Equal(PetVisualState.KeyboardLeft, controller.CurrentState);

        controller.ApplyInput(right, true, Start.AddMilliseconds(10));
        Assert.Equal(PetVisualState.KeyboardBoth, controller.CurrentState);

        controller.ApplyInput(left, false, Start.AddMilliseconds(20));
        Assert.Equal(PetVisualState.KeyboardRight, controller.CurrentState);

        controller.ApplyInput(right, false, Start.AddMilliseconds(120));
        Assert.Equal(PetVisualState.Idle, controller.CurrentState);
    }

    [Fact]
    public void ReleasedActionRemainsVisibleForMinimumDuration()
    {
        var controller = new PetAnimationController(Start);
        var key = new InputId(0x04);

        controller.ApplyInput(key, true, Start);
        controller.ApplyInput(key, false, Start.AddMilliseconds(10));

        Assert.Equal(PetVisualState.KeyboardLeft, controller.CurrentState);
        Assert.Equal(Start + PetAnimationController.MinimumActionDuration, controller.NextTransitionAt);
        Assert.False(controller.Advance(Start.AddMilliseconds(89)));
        Assert.True(controller.Advance(Start.AddMilliseconds(90)));
        Assert.Equal(PetVisualState.Idle, controller.CurrentState);
        Assert.Null(controller.NextTransitionAt);
    }

    [Fact]
    public void KeyboardRepeatDoesNotRestartActionOrCreateQueuedWork()
    {
        var controller = new PetAnimationController(Start);
        var key = new InputId(0x04);

        var firstPress = controller.ApplyInput(key, true, Start);
        var repeatedPress = controller.ApplyInput(key, true, Start.AddMilliseconds(80));
        var release = controller.ApplyInput(key, false, Start.AddMilliseconds(95));

        Assert.True(firstPress.StateChanged);
        Assert.True(firstPress.PulseRequested);
        Assert.False(repeatedPress.StateChanged);
        Assert.False(repeatedPress.PulseRequested);
        Assert.True(release.StateChanged);
        Assert.False(release.PulseRequested);

        Assert.Equal(PetVisualState.Idle, controller.CurrentState);
        Assert.Null(controller.NextTransitionAt);
    }

    [Fact]
    public void MultipleMouseButtonsFollowDocumentedPriority()
    {
        var controller = new PetAnimationController(Start);

        controller.ApplyInput(InputId.MouseForward, true, Start);
        controller.ApplyInput(InputId.MouseBack, true, Start.AddMilliseconds(1));
        controller.ApplyInput(InputId.MouseMiddle, true, Start.AddMilliseconds(2));
        controller.ApplyInput(InputId.MouseRight, true, Start.AddMilliseconds(3));
        controller.ApplyInput(InputId.MouseLeft, true, Start.AddMilliseconds(4));
        Assert.Equal(PetVisualState.MouseLeft, controller.CurrentState);

        controller.ApplyInput(InputId.MouseLeft, false, Start.AddMilliseconds(5));
        Assert.Equal(PetVisualState.MouseRight, controller.CurrentState);
        controller.ApplyInput(InputId.MouseRight, false, Start.AddMilliseconds(6));
        Assert.Equal(PetVisualState.MouseMiddle, controller.CurrentState);
        controller.ApplyInput(InputId.MouseMiddle, false, Start.AddMilliseconds(7));
        Assert.Equal(PetVisualState.MouseBack, controller.CurrentState);
        controller.ApplyInput(InputId.MouseBack, false, Start.AddMilliseconds(8));
        Assert.Equal(PetVisualState.MouseForward, controller.CurrentState);
    }

    [Theory]
    [InlineData(1001, PetVisualState.MouseLeft)]
    [InlineData(1002, PetVisualState.MouseRight)]
    [InlineData(1003, PetVisualState.MouseMiddle)]
    [InlineData(1004, PetVisualState.MouseBack)]
    [InlineData(1005, PetVisualState.MouseForward)]
    public void EachMouseButtonPressAndReleaseDrivesItsOwnState(
        ushort inputValue,
        PetVisualState expectedState)
    {
        var controller = new PetAnimationController(Start);
        var input = new InputId(inputValue);

        Assert.True(controller.ApplyInput(input, true, Start).StateChanged);
        Assert.Equal(expectedState, controller.CurrentState);
        Assert.True(controller.ApplyInput(input, false, Start.AddMilliseconds(100)).StateChanged);
        Assert.Equal(PetVisualState.Idle, controller.CurrentState);
    }

    [Fact]
    public void WheelNeverChangesPetState()
    {
        var controller = new PetAnimationController(Start);

        var wheelUp = controller.ApplyInput(InputId.WheelUp, true, Start);
        var wheelDown = controller.ApplyInput(InputId.WheelDown, false, Start.AddMilliseconds(1));

        Assert.False(wheelUp.StateChanged);
        Assert.False(wheelUp.PulseRequested);
        Assert.False(wheelDown.StateChanged);
        Assert.False(wheelDown.PulseRequested);
        Assert.Equal(PetVisualState.Idle, controller.CurrentState);
    }

    [Fact]
    public void NewKeyOnSameHandRequestsPulseWithoutChangingFrameState()
    {
        var controller = new PetAnimationController(Start);
        var firstKey = new InputId(0x04); // A
        var secondKey = new InputId(0x16); // S

        var firstPress = controller.ApplyInput(firstKey, true, Start);
        var secondPress = controller.ApplyInput(secondKey, true, Start.AddMilliseconds(20));
        var repeatedSecondPress = controller.ApplyInput(secondKey, true, Start.AddMilliseconds(40));

        Assert.True(firstPress.StateChanged);
        Assert.True(firstPress.PulseRequested);
        Assert.False(secondPress.StateChanged);
        Assert.True(secondPress.PulseRequested);
        Assert.False(repeatedSecondPress.StateChanged);
        Assert.False(repeatedSecondPress.PulseRequested);
        Assert.Equal(PetVisualState.KeyboardLeft, controller.CurrentState);
    }

    [Fact]
    public void PauseClearsPressedStateAndResumeReturnsToIdle()
    {
        var controller = new PetAnimationController(Start);
        controller.ApplyInput(new InputId(0x04), true, Start);

        Assert.True(controller.SetPaused(true, Start.AddMilliseconds(1)));
        Assert.Equal(PetVisualState.Paused, controller.CurrentState);
        Assert.True(controller.SetPaused(false, Start.AddMilliseconds(2)));
        Assert.Equal(PetVisualState.Idle, controller.CurrentState);
    }
}
