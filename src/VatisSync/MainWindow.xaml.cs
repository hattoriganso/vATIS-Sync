using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using VatisSync.ViewModels;

namespace VatisSync;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;

    /// <summary>Set by the app when the user really wants to exit (tray menu), so Close() is not turned into hide.</summary>
    public bool AllowClose { get; set; }

    public MainWindow(MainViewModel vm)
    {
        InitializeComponent();
        DataContext = _vm = vm;

        // Keep the newest log line in view.
        ((INotifyCollectionChanged)_vm.LogLines).CollectionChanged += (_, e) =>
        {
            if (e.Action == NotifyCollectionChangedAction.Add && LogList.Items.Count > 0)
                LogList.ScrollIntoView(LogList.Items[^1]);
        };
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!AllowClose && _vm.CloseToTray)
        {
            e.Cancel = true;
            Hide();
        }
        base.OnClosing(e);
    }
}
