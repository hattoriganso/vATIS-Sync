using System.Drawing;
using System.Windows;
using System.Windows.Controls;
using H.NotifyIcon;
using VatisSync.Core;
using VatisSync.Services;
using VatisSync.ViewModels;

namespace VatisSync;

public partial class App : Application
{
    private const string InstanceName = @"Local\VatisSync.SingleInstance";
    private const string ShowEventName = @"Local\VatisSync.Show";

    private Mutex? _mutex;
    private EventWaitHandle? _showEvent;
    private TaskbarIcon? _tray;
    private MainWindow? _window;
    private MainViewModel? _vm;
    private readonly ErrorLog _errorLog = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        new SafetyNet(this, _errorLog, () => _vm).Install();

        // Colors come from a palette file; Windows High Contrast gets its own palette, and switching it on or off applies live.
        ApplyPalette();
        SystemParameters.StaticPropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(SystemParameters.HighContrast)) Dispatcher.BeginInvoke(ApplyPalette);
        };

        _mutex = new Mutex(initiallyOwned: true, InstanceName, out var isFirst);
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        if (!isFirst)
        {
            // Already running: ask that copy to show itself, then leave.
            _showEvent.Set();
            Shutdown();
            return;
        }
        ListenForShowRequests();

        var approver = new WpfUpdateApprover();
        approver.Asked = request => _tray?.ShowNotification("ATIS change needs your approval",
            $"{request.Airport}: {request.Summary}");
        _vm = new MainViewModel(
            applyTray: visible => { if (_tray is not null) _tray.Visibility = Visibility.Visible; },
            approver: approver);
        _window = new MainWindow(_vm);
        CreateTray();
        _window.HiddenToTray += () =>
        {
            if (_vm.TakeTrayHint())
                _tray?.ShowNotification("vATIS-Sync is still running",
                    "It stays in the tray so syncing continues. Right-click the icon and choose Exit to quit.");
        };

        if (!_vm.ShouldStartHidden) _window.Show();
        _vm.StartIfConfigured();
        _vm.StartDiscovery(); // read the airports from the open vATIS profile, retrying until vATIS answers
    }

    private void ApplyPalette()
    {
        // VATISSYNC_FORCE_HC=1 shows the High Contrast palette without switching Windows into High Contrast (for checking it).
        var highContrast = SystemParameters.HighContrast || Environment.GetEnvironmentVariable("VATISSYNC_FORCE_HC") == "1";
        var file = highContrast ? "Themes/Palette.HighContrast.xaml" : "Themes/Palette.Light.xaml";
        Resources.MergedDictionaries[0] = new ResourceDictionary { Source = new Uri(file, UriKind.Relative) };
    }

    private void ListenForShowRequests()
    {
        var thread = new Thread(() =>
        {
            while (_showEvent!.WaitOne())
                Dispatcher.BeginInvoke(ShowWindow);
        }) { IsBackground = true, Name = "show-request-listener" };
        thread.Start();
    }

    private void CreateTray()
    {
        var menu = new ContextMenu();
        var open = new MenuItem { Header = "Open vATIS-Sync" };
        open.Click += (_, _) => ShowWindow();
        var toggle = new MenuItem { Header = "Start / Stop" };
        toggle.Click += (_, _) => _vm!.StartStopCommand.Execute(null);
        var exit = new MenuItem { Header = "Exit" };
        exit.Click += async (_, _) => await ExitAppAsync();
        menu.Items.Add(open);
        menu.Items.Add(toggle);
        menu.Items.Add(new Separator());
        menu.Items.Add(exit);

        _tray = new TaskbarIcon
        {
            ToolTipText = "vATIS-Sync",
            ContextMenu = menu,
            Icon = MakeIcon(),
            NoLeftClickDelay = true,
        };
        _tray.TrayLeftMouseUp += (_, _) => ShowWindow();
        _tray.ForceCreate();
    }

    private void ShowWindow()
    {
        if (_window is null) return;
        _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    private async Task ExitAppAsync()
    {
        if (_vm is not null)
        {
            await _vm.StopDiscoveryAsync();
            await _vm.StopAsync();
        }
        if (_window is not null) { _window.AllowClose = true; _window.Close(); }
        _tray?.Dispose();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _showEvent?.Dispose();
        try { _mutex?.ReleaseMutex(); } catch (ApplicationException) { }
        _mutex?.Dispose();
        base.OnExit(e);
    }

    /// <summary>Placeholder icon drawn at runtime until a real one exists: a white "A" on blue.</summary>
    private static Icon MakeIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            using var bg = new SolidBrush(Color.FromArgb(0x1F, 0x6F, 0xD6));
            g.FillEllipse(bg, 1, 1, 30, 30);
            using var font = new Font("Segoe UI", 16, System.Drawing.FontStyle.Bold, GraphicsUnit.Pixel);
            var size = g.MeasureString("A", font);
            g.DrawString("A", font, Brushes.White, (32 - size.Width) / 2, (32 - size.Height) / 2);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }
}
