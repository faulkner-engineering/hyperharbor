namespace HyperHarbor.Host.Tray;

/// <summary>
/// Keeps tray windows inside the screen's work area, which shrinks in logical terms as display scaling grows.
/// </summary>
internal static class WindowFit
{
    /// <summary>Share of the work area left free around a window sized to fit it.</summary>
    private const double Margin = 0.05;

    /// <summary>The preferred size, capped at the work area less a margin, and never below <paramref name="minimum"/>.</summary>
    public static Size Fit(Size preferred, Rectangle workingArea, Size minimum)
    {
        var maximumWidth = (int)(workingArea.Width * (1 - Margin));
        var maximumHeight = (int)(workingArea.Height * (1 - Margin));
        return new Size(
            Math.Max(minimum.Width, Math.Min(preferred.Width, maximumWidth)),
            Math.Max(minimum.Height, Math.Min(preferred.Height, maximumHeight)));
    }

    /// <summary>Moves <paramref name="bounds"/> into the work area, shrinking it first if it is larger.</summary>
    public static Rectangle PlaceInside(Rectangle bounds, Rectangle workingArea)
    {
        var width = Math.Min(bounds.Width, workingArea.Width);
        var height = Math.Min(bounds.Height, workingArea.Height);
        var x = Math.Clamp(bounds.X, workingArea.Left, workingArea.Right - width);
        var y = Math.Clamp(bounds.Y, workingArea.Top, workingArea.Bottom - height);
        return new Rectangle(x, y, width, height);
    }

    /// <summary>Centers a window of <paramref name="size"/> in the work area.</summary>
    public static Rectangle Center(Size size, Rectangle workingArea) => PlaceInside(
        new Rectangle(
            workingArea.Left + ((workingArea.Width - size.Width) / 2),
            workingArea.Top + ((workingArea.Height - size.Height) / 2),
            size.Width,
            size.Height),
        workingArea);
}
