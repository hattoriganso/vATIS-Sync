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
    private ReviewPrompt? _prompt;
    private readonly ErrorLog _errorLog = new();
    private readonly ActivityLog _activity = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // UI checks (screenshots with made-up data) host this app's resources and windows, but must never start the real
        // app: that reads the user's real settings and talks to their real vATIS. They set this variable to stop here.
        if (Environment.GetEnvironmentVariable("VATISSYNC_NO_STARTUP") == "1") return;

        new SafetyNet(this, _errorLog, () => _vm, _activity).Install();
        EngineFactory.Log = _activity;

        // Colors come from a palette file: light, dark, or Windows High Contrast. Until the saved choice is read, follow Windows.
        ThemeService.Instance.Apply(ThemePreference.System);

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
        WriteStartupRecord();

        // A change waiting for review never opens a window by itself: a small strip appears in a corner and the
        // comparison window opens when the controller clicks Review.
        var reviews = new ReviewQueue();
        reviews.Asked += request => Dispatcher.BeginInvoke(() => _tray?.ShowNotification("ATIS change waiting for your review",
            $"{request.Airport}: {request.FlowSummary}"));
        _vm = new MainViewModel(
            applyTray: visible => { if (_tray is not null) _tray.Visibility = Visibility.Visible; },
            approver: reviews,
            activityLog: _activity);
        _prompt = new ReviewPrompt(reviews, () => _vm.ReviewSound);
        _vm.ReviewRequested = _prompt.Review;
        _activity.TraceEnabled = _vm.DetailedLog;

        // The saved light / dark / follow-Windows choice, applied before the first window exists so nothing flashes the wrong colors.
        ThemeService.Instance.Changed += mode => _vm.ShownTheme = mode;
        ThemeService.Instance.Apply(_vm.Theme);
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.Theme)) ThemeService.Instance.Apply(_vm.Theme);
        };
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
        var review = new MenuItem { Header = "Review waiting changes" };
        review.Click += (_, _) => _prompt?.ReviewNext();
        menu.Opened += (_, _) => review.IsEnabled = _prompt?.HasWaiting == true;
        var toggle = new MenuItem { Header = "Start / Stop" };
        toggle.Click += (_, _) => _vm!.StartStopCommand.Execute(null);
        var exit = new MenuItem { Header = "Exit" };
        exit.Click += async (_, _) => await ExitAppAsync();
        menu.Items.Add(open);
        menu.Items.Add(review);
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

    /// <summary>The first lines of a run: enough to know what was running, and on what, when reading a log afterwards.</summary>
    private void WriteStartupRecord()
    {
        var version = System.Reflection.Assembly.GetExecutingAssembly()
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion ?? "unknown";
        _activity.Write($"vATIS-Sync {version} started (process {Environment.ProcessId}, " +
                        $"{System.Runtime.InteropServices.RuntimeInformation.OSDescription}, " +
                        $"{System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}, " +
                        $"{System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture})");
        _activity.Write($"settings file {SettingsStore.DefaultPath}; log files {_activity.Directory}");
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _activity.Write("vATIS-Sync exiting");
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
