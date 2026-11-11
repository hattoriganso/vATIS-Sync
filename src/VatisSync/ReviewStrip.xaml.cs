using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
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

    /// <summary>The chosen corner of the main screen's work area, clear of the taskbar.</summary>
    private void PlaceInCorner()
    {
        var area = SystemParameters.WorkArea;
        const double gap = 16;
        Left = Corner is ScreenCorner.BottomLeft or ScreenCorner.TopLeft ? area.Left + gap : area.Right - ActualWidth - gap;
        Top = Corner is ScreenCorner.TopLeft or ScreenCorner.TopRight ? area.Top + gap : area.Bottom - ActualHeight - gap;
    }

    private void OnReview(object sender, RoutedEventArgs e) => ReviewClicked?.Invoke();
    private void OnLater(object sender, RoutedEventArgs e) => LaterClicked?.Invoke();
}
