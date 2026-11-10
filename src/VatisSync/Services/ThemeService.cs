using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;
using VatisSync.Core;

namespace VatisSync.Services;

/// <summary>
/// Puts the right palette on the app: the user's choice (light, dark, or follow Windows), unless Windows High Contrast
/// is on, which always wins. It follows Windows live while the choice is "follow Windows", and keeps every window's
/// title bar (drawn by Windows, not by the app) in step with the colors.
/// </summary>
public sealed class ThemeService
{
    /// <summary>One for the app. Windows register with it, and it holds the current choice.</summary>
    public static ThemeService Instance { get; } = new();

    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    private bool _watching;

    public ThemePreference Preference { get; private set; } = ThemePreference.System;

    /// <summary>What is showing right now.</summary>
    public ThemeMode Mode { get; private set; } = ThemeMode.Light;

    public bool IsDark => Mode == ThemeMode.Dark;

    /// <summary>Raised on the UI thread whenever the shown theme is (re)applied.</summary>
    public event Action<ThemeMode>? Changed;

    /// <summary>Applies the choice, and from then on follows Windows (light or dark, and High Contrast) as it changes.</summary>
    public void Apply(ThemePreference preference)
    {
        Preference = preference;
        Watch();
        Refresh();
    }

    /// <summary>Gives a window the title bar that matches. Call from its constructor.</summary>
    public void Attach(Window window)
    {
        void Recolor(ThemeMode _) => TitleBar.Apply(window, IsDark, redraw: true);
        window.SourceInitialized += (_, _) => TitleBar.Apply(window, IsDark, redraw: false);
        Changed += Recolor;
        window.Closed += (_, _) => Changed -= Recolor; // windows come and go (the approval question); do not keep them alive
    }

    private void Refresh()
    {
        var app = Application.Current;
        if (app is null) return;

        // VATISSYNC_FORCE_HC=1 shows the High Contrast palette without switching Windows into High Contrast (for checking it).
        var highContrast = SystemParameters.HighContrast || Environment.GetEnvironmentVariable("VATISSYNC_FORCE_HC") == "1";
        Mode = ThemeRules.Resolve(Preference, WindowsIsDark(), highContrast);

        var file = Mode switch
        {
            ThemeMode.HighContrast => "Themes/Palette.HighContrast.xaml",
            ThemeMode.Dark => "Themes/Palette.Dark.xaml",
            _ => "Themes/Palette.Light.xaml",
        };
        // Every color is looked up by name, dynamically, so swapping this one dictionary recolors everything already open.
        // The full pack address names this assembly, so it resolves whichever program is hosting the app.
        app.Resources.MergedDictionaries[0] = new ResourceDictionary
        {
            Source = new Uri($"pack://application:,,,/{typeof(ThemeService).Assembly.GetName().Name};component/{file}", UriKind.Absolute),
        };

        Changed?.Invoke(Mode);
    }

    private void Watch()
    {
        if (_watching) return;
        _watching = true;

        // Windows switching between light and dark arrives as a "general" user-preference change, on another thread.
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category == UserPreferenceCategory.General) Application.Current?.Dispatcher.BeginInvoke(Refresh);
        };
        SystemParameters.StaticPropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SystemParameters.HighContrast)) Application.Current?.Dispatcher.BeginInvoke(Refresh);
        };
    }

    /// <summary>Windows' own light/dark choice for apps. Light if it cannot be read (older Windows has no such choice).</summary>
    private static bool WindowsIsDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>
/// The title bar and frame of a window are drawn by Windows, so WPF colors do not reach them. Windows 10 (1903 and later)
/// and 11 take a per-window "immersive dark mode" switch; on anything older the call fails and the title bar stays light,
/// which is cosmetic only.
/// </summary>
internal static class TitleBar
{
    private const int UseImmersiveDarkMode = 20;           // Windows 10 2004 and later, and Windows 11
    private const int UseImmersiveDarkModeBefore20H1 = 19; // Windows 10 1903 and 1909
    private const uint SwpFlags = 0x0002 | 0x0001 | 0x0004 | 0x0020 | 0x0010; // no move, size, z-order; frame changed; no activate

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    public static void Apply(Window window, bool dark, bool redraw)
    {
        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return; // not created yet: SourceInitialized will call again

            var on = dark ? 1 : 0;
            if (DwmSetWindowAttribute(hwnd, UseImmersiveDarkMode, ref on, sizeof(int)) != 0)
                DwmSetWindowAttribute(hwnd, UseImmersiveDarkModeBefore20H1, ref on, sizeof(int));

            // A window that is already showing does not repaint its title bar by itself.
            if (redraw) SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, SwpFlags);
        }
        catch
        {
            // Cosmetic only: never let a title bar stop the app.
        }
    }
}
