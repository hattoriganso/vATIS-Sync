using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
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
        DataContext = _vm = vm;

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

    private void OnOpenSettings(object sender, RoutedEventArgs e) =>
        new SettingsWindow(_vm) { Owner = this }.ShowDialog();

    private void OnRemoveAirport(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: AirportViewModel airport })
            _vm.RemoveAirportCommand.Execute(airport);
    }

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
