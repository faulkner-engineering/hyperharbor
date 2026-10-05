using System.Drawing;
using HyperHarbor.Host.Tray;

namespace HyperHarbor.Host.Tests.Tray;

public sealed class WindowFitTests
{
    // A 1920x1080 display at 200% scaling: 1920x1032 physical pixels of work area above the taskbar.
    private static readonly Rectangle WorkArea = new(0, 0, 1920, 1032);

    [Fact]
    public void APreferredSizeTallerThanTheWorkArea_IsCapped_WithAMargin()
    {
        var size = WindowFit.Fit(new Size(1000, 1600), WorkArea, new Size(400, 300));

        Assert.Equal(1000, size.Width);
        Assert.InRange(size.Height, 900, 1031);
    }

    [Fact]
    public void APreferredSizeWiderThanTheWorkArea_IsCapped()
    {
        var size = WindowFit.Fit(new Size(4000, 600), WorkArea, new Size(400, 300));

        Assert.InRange(size.Width, 1700, 1919);
        Assert.Equal(600, size.Height);
    }

    [Fact]
    public void TheMinimum_WinsOverATinyWorkArea()
    {
        var size = WindowFit.Fit(new Size(800, 600), new Rectangle(0, 0, 300, 200), new Size(400, 300));

        Assert.Equal(new Size(400, 300), size);
    }

    [Fact]
    public void AWindowBelowAndRightOfTheWorkArea_IsMovedInside()
    {
        var placed = WindowFit.PlaceInside(new Rectangle(1800, 900, 600, 500), WorkArea);

        Assert.Equal(new Rectangle(1320, 532, 600, 500), placed);
    }

    [Fact]
    public void AWindowLargerThanTheWorkArea_IsShrunkToIt()
    {
        var placed = WindowFit.PlaceInside(new Rectangle(100, -50, 800, 2000), WorkArea);

        Assert.Equal(new Rectangle(100, 0, 800, 1032), placed);
    }

    [Fact]
    public void AWindowOnAMonitorLeftOfThePrimary_StaysThere()
    {
        var left = new Rectangle(-2560, -400, 2560, 1400);

        var placed = WindowFit.PlaceInside(new Rectangle(-2000, 0, 800, 600), left);
        var centered = WindowFit.Center(new Size(800, 600), left);

        Assert.Equal(new Rectangle(-2000, 0, 800, 600), placed);
        Assert.Equal(new Rectangle(-1680, 0, 800, 600), centered);
    }
}
