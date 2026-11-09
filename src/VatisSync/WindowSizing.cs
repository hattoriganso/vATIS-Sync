using System.Windows;

namespace VatisSync;

/// <summary>Keeps a window's opening size inside the screen, so it never opens taller than a small or highly scaled display.</summary>
internal static class WindowSizing
{
    /// <summary>Shrinks Width/Height to fit the usable screen area (and lowers its minimum if even that would not fit).</summary>
    public static void FitToScreen(Window window) => FitToScreen(window, SystemParameters.WorkArea.Width, SystemParameters.WorkArea.Height);

    internal static void FitToScreen(Window window, double availableWidth, double availableHeight)
    {
        // Leave a little room around the window for the taskbar edge and shadow.
        var width = availableWidth - 24;
        var height = availableHeight - 24;
        window.MinWidth = Math.Min(window.MinWidth, width);
        window.MinHeight = Math.Min(window.MinHeight, height);
        window.Width = Math.Min(window.Width, width);
        window.Height = Math.Min(window.Height, height);
    }
}
