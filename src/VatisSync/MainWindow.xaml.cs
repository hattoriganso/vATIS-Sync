using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using VatisSync.Services;
using VatisSync.ViewModels;

namespace VatisSync;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;

    /// <summary>Set by the app when the user really wants to exit (tray menu), so Close() is not turned into hide.</summary>
    public bool AllowClose { get; set; }

    /// <summary>Raised when closing the window only hid it (the app keeps running in the tray).</summary>
    public event Action? HiddenToTray;

    public MainWindow(MainViewModel vm)
    {
        InitializeComponent();
        ThemeService.Instance.Attach(this);
        WindowSizing.FitToScreen(this);
        DataContext = _vm = vm;

        // Showing the newest log line asks every scroller above it to bring that line into view too, which scrolled the whole
        // Overview down past its title. The log scrolls itself; the page must stay where the controller left it.
        LogList.RequestBringIntoView += (_, e) => e.Handled = true;

        // Keep the newest log line in view. This must not touch the list while the collection is still announcing the
        // change: the list may not have processed it yet, and measuring it then fails ("inconsistent with its items
        // source"). So scroll once the dispatcher is free.
        ((INotifyCollectionChanged)_vm.LogLines).CollectionChanged += (_, e) =>
        {
            if (e.Action != NotifyCollectionChangedAction.Add) return;
            Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
            {
                if (LogList.Items.Count > 0) LogList.ScrollIntoView(LogList.Items[^1]);
            });
        };
    }

    /// <summary>The quick switch: a small menu of the three choices, with a tick on the current one.</summary>
    private void OnOpenThemeMenu(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = ThemeButton, Placement = PlacementMode.Bottom };
        foreach (var choice in MainViewModel.ThemeChoices)
        {
            var item = new MenuItem { Header = choice.Label, IsChecked = _vm.Theme == choice.Value };
            item.Click += (_, _) => _vm.Theme = choice.Value;
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    private void OnOpenSettings(object sender, RoutedEventArgs e) =>
        new SettingsWindow(_vm) { Owner = this }.ShowDialog();

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!AllowClose && _vm.CloseToTray)
        {
            e.Cancel = true;
            Hide();
            HiddenToTray?.Invoke();
        }
        base.OnClosing(e);
    }
}
