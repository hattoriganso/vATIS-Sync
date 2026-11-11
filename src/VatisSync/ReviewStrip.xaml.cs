using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using VatisSync.Core;
using VatisSync.Services;

namespace VatisSync;

/// <summary>
/// Tells the controller that a change is waiting, without taking the screen or the keyboard: a small strip in the corner
/// that stays on top but is never the active window, so nothing typed in CRC or vATIS can land on it.
/// </summary>
public partial class ReviewStrip : Window
{
    private const int GwlExStyle = -20;
    private const int WsExNoActivate = 0x08000000;
    private const int WsExToolWindow = 0x00000080;

    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hwnd, int index, int value);

    public ReviewStrip()
    {
        InitializeComponent();
        ThemeService.Instance.Attach(this);
        SizeChanged += (_, _) => { if (AutoPlace) PlaceInCorner(); };
    }

    /// <summary>Put itself in the corner of the screen. Only a UI check (screenshots) turns this off.</summary>
    public bool AutoPlace { get; set; } = true;

    /// <summary>Review was clicked.</summary>
    public event Action? ReviewClicked;

    /// <summary>Later was clicked.</summary>
    public event Action? LaterClicked;

    /// <summary>The line under the heading: the airport and its runway change, or how many airports are waiting.</summary>
    public string DetailText
    {
        get => Detail.Text;
        set
        {
            Detail.Text = value;
            ToolTip = value;
        }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // Never activated, never in Alt+Tab: it can be clicked, but it cannot take focus from whatever is being typed into.
        var handle = new WindowInteropHelper(this).Handle;
        SetWindowLong(handle, GwlExStyle, GetWindowLong(handle, GwlExStyle) | WsExNoActivate | WsExToolWindow);
        if (AutoPlace) PlaceInCorner();
    }

    /// <summary>The corner the strip sits in. Set before it is shown.</summary>
    public ScreenCorner Corner { get; set; } = ScreenCorner.BottomRight;

    /// <summary>The monitor to appear on (its Windows device name). Empty, or a monitor that is gone, means the main screen.</summary>
    public string ScreenName { get; set; } = "";

    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out WindowRect rect);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowRect { public int Left, Top, Right, Bottom; }

    private const uint SwpNoSize = 0x0001, SwpNoZOrder = 0x0004, SwpNoActivate = 0x0010;

    /// <summary>
    /// The chosen corner of the chosen monitor's work area, clear of the taskbar. Placed in pixels with the window's own size,
    /// so it lands right on a monitor with a different scaling than the main one.
    /// </summary>
    private void PlaceInCorner()
    {
        var handle = new WindowInteropHelper(this).Handle;
        var screen = ScreenPicker.Pick(Screens.Connected(), ScreenName);
        if (handle == IntPtr.Zero || screen is null || !GetWindowRect(handle, out var size))
        {
            // Windows gave us nothing to work with: the main screen, as WPF sees it.
            var area = SystemParameters.WorkArea;
            const double gap = 16;
            Left = Corner is ScreenCorner.BottomLeft or ScreenCorner.TopLeft ? area.Left + gap : area.Right - ActualWidth - gap;
            Top = Corner is ScreenCorner.TopLeft or ScreenCorner.TopRight ? area.Top + gap : area.Bottom - ActualHeight - gap;
            return;
        }

        var scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var (x, y) = screen.Place(Corner, size.Right - size.Left, size.Bottom - size.Top, (int)Math.Round(16 * scale));
        SetWindowPos(handle, IntPtr.Zero, x, y, 0, 0, SwpNoSize | SwpNoZOrder | SwpNoActivate);
    }

    private void OnReview(object sender, RoutedEventArgs e) => ReviewClicked?.Invoke();
    private void OnLater(object sender, RoutedEventArgs e) => LaterClicked?.Invoke();
}
