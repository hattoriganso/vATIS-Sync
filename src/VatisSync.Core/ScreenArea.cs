namespace VatisSync.Core;

/// <summary>The usable part of one monitor (clear of the taskbar), in the pixels Windows reports.</summary>
public sealed record ScreenArea(string DeviceName, int Left, int Top, int Right, int Bottom, bool IsPrimary)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;

    /// <summary>Where the top left of a box of this size goes to sit in the corner, <paramref name="gap"/> pixels from the edges.</summary>
    public (int X, int Y) Place(ScreenCorner corner, int width, int height, int gap)
    {
        var x = corner is ScreenCorner.BottomLeft or ScreenCorner.TopLeft ? Left + gap : Right - width - gap;
        var y = corner is ScreenCorner.TopLeft or ScreenCorner.TopRight ? Top + gap : Bottom - height - gap;
        // A box bigger than the screen still keeps its top left corner on it.
        return (Math.Max(x, Left), Math.Max(y, Top));
    }
}

public static class ScreenPicker
{
    /// <summary>
    /// The screen the controller asked for, or the main screen when they asked for none, when it is no longer connected,
    /// or when Windows reported nothing at all (then null: the caller keeps its own default).
    /// </summary>
    public static ScreenArea? Pick(IReadOnlyList<ScreenArea> screens, string? wanted)
    {
        if (!string.IsNullOrWhiteSpace(wanted)
            && screens.FirstOrDefault(s => string.Equals(s.DeviceName, wanted, StringComparison.OrdinalIgnoreCase)) is { } match)
            return match;
        return screens.FirstOrDefault(s => s.IsPrimary) ?? screens.FirstOrDefault();
    }
}
