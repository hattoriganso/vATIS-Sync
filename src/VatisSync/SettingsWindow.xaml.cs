using System.Windows;
using VatisSync.Services;
using VatisSync.ViewModels;

namespace VatisSync;

/// <summary>General options and the defaults every airport follows. Shares the main window's view model, so changes save as you make them.</summary>
public partial class SettingsWindow : Window
{
    public SettingsWindow(MainViewModel vm)
    {
        InitializeComponent();
        ThemeService.Instance.Attach(this);
        WindowSizing.FitToScreen(this);
        DataContext = vm;
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
