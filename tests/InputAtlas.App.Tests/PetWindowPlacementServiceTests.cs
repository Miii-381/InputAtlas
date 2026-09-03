using System.Windows;
using InputAtlas.App.Pet;

namespace InputAtlas.App.Tests;

public sealed class PetWindowPlacementServiceTests
{
    [Fact]
    public void DefaultPositionUsesBottomRightOfPrimaryWorkAreaWithMargin()
    {
        var result = PetWindowPlacementService.GetDefaultPosition(
            new Rect(0, 0, 1920, 1040),
            new Size(420, 336));

        Assert.Equal(new Point(1476, 680), result);
    }

    [Fact]
    public void ClampKeepsAtLeastMarginVisibleInsideWorkArea()
    {
        var workArea = new Rect(100, 50, 1200, 800);
        var size = new Size(420, 336);

        var tooFarLeft = PetWindowPlacementService.Clamp(workArea, size, new Point(-1000, 100));
        var tooFarBottom = PetWindowPlacementService.Clamp(workArea, size, new Point(500, 2000));

        Assert.Equal(100, tooFarLeft.X);
        Assert.Equal(100, tooFarLeft.Y);
        Assert.Equal(500, tooFarBottom.X);
        Assert.Equal(514, tooFarBottom.Y);
    }

    [Fact]
    public void ClampPreservesAlreadyVisiblePosition()
    {
        var result = PetWindowPlacementService.Clamp(
            new Rect(0, 0, 1920, 1080),
            new Size(420, 336),
            new Point(1200, 700));

        Assert.Equal(new Point(1200, 700), result);
    }

    [Fact]
    public void SnapLocksEachAxisToNearestWorkAreaEdgeWithinThreshold()
    {
        var workArea = new Rect(100, 50, 1200, 760);
        var size = new Size(420, 336);

        var result = PetWindowPlacementService.Snap(
            workArea,
            size,
            new Point(111, 465));

        Assert.True(result.Horizontal);
        Assert.True(result.Vertical);
        Assert.Equal(new Point(100, 474), result.Position);
    }

    [Fact]
    public void SnapLeavesPositionUnchangedOutsideThreshold()
    {
        var requested = new Point(430, 280);

        var result = PetWindowPlacementService.Snap(
            new Rect(100, 50, 1200, 760),
            new Size(420, 336),
            requested);

        Assert.False(result.Horizontal);
        Assert.False(result.Vertical);
        Assert.Equal(requested, result.Position);
    }

    [Fact]
    public void SnapReleasesAfterProposedPositionMovesBeyondThreshold()
    {
        var workArea = new Rect(0, 0, 1920, 1040);
        var size = new Size(420, 336);
        var snapped = PetWindowPlacementService.Snap(
            workArea,
            size,
            new Point(1510, 714));
        var releasedPosition = new Point(1480, 680);
        var released = PetWindowPlacementService.Snap(
            workArea,
            size,
            releasedPosition);

        Assert.True(snapped.Horizontal);
        Assert.True(snapped.Vertical);
        Assert.Equal(new Point(1500, 704), snapped.Position);
        Assert.False(released.Horizontal);
        Assert.False(released.Vertical);
        Assert.Equal(releasedPosition, released.Position);
    }

    [Fact]
    public void SnapUsesNegativeMonitorWorkAreaCoordinates()
    {
        var result = PetWindowPlacementService.Snap(
            new Rect(-1920, -120, 1920, 1080),
            new Size(420, 336),
            new Point(-1910, 615));

        Assert.True(result.Horizontal);
        Assert.True(result.Vertical);
        Assert.Equal(new Point(-1920, 624), result.Position);
    }
}
