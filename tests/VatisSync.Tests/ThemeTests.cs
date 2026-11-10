using System.Globalization;
using System.Xml.Linq;
using VatisSync.Core;

namespace VatisSync.Tests;

public class ThemeTests : TempDirTests
{
    // ---- which theme is shown ----

    [Theory]
    [InlineData(ThemePreference.System, false, false, ThemeMode.Light)]
    [InlineData(ThemePreference.System, true, false, ThemeMode.Dark)]
    [InlineData(ThemePreference.Light, true, false, ThemeMode.Light)]  // an explicit choice beats Windows
    [InlineData(ThemePreference.Dark, false, false, ThemeMode.Dark)]
    [InlineData(ThemePreference.System, true, true, ThemeMode.HighContrast)] // High Contrast beats everything
    [InlineData(ThemePreference.Dark, false, true, ThemeMode.HighContrast)]
    [InlineData(ThemePreference.Light, false, true, ThemeMode.HighContrast)]
    public void Resolve_picks_the_theme_to_show(ThemePreference pick, bool windowsDark, bool highContrast, ThemeMode expected) =>
        Assert.Equal(expected, ThemeRules.Resolve(pick, windowsDark, highContrast));

    // ---- the saved choice ----

    [Theory]
    [InlineData("Dark", ThemePreference.Dark)]
    [InlineData("dark", ThemePreference.Dark)]
    [InlineData("Light", ThemePreference.Light)]
    [InlineData("System", ThemePreference.System)]
    [InlineData("sepia", ThemePreference.System)] // unknown: follow Windows, keep every other setting
    [InlineData("42", ThemePreference.System)]
    [InlineData("", ThemePreference.System)]
    public void The_saved_choice_is_read_tolerantly_and_nothing_else_is_lost(string value, ThemePreference expected)
    {
        var loaded = new SettingsStore(Write("s.json", $"{{ \"theme\": \"{value}\", \"intervalMinutes\": 15 }}")).Load();
        Assert.Equal(expected, loaded.Theme);
        Assert.Equal(15, loaded.IntervalMinutes);
    }

    [Fact]
    public void The_default_is_to_follow_windows_and_a_number_or_missing_value_does_too()
    {
        Assert.Equal(ThemePreference.System, new AppSettings().Theme);
        Assert.Equal(ThemePreference.System, new SettingsStore(Write("a.json", "{ \"intervalMinutes\": 7 }")).Load().Theme);
        Assert.Equal(ThemePreference.System, new SettingsStore(Write("b.json", "{ \"theme\": 2 }")).Load().Theme);
    }

    [Fact]
    public void The_choice_is_remembered()
    {
        var store = new SettingsStore(Path.Combine(Dir, "sub", "settings.json"));
        store.Save(new AppSettings { Theme = ThemePreference.Dark });
        Assert.Equal(ThemePreference.Dark, store.Load().Theme);
        Assert.Contains("\"Dark\"", File.ReadAllText(store.FilePath)); // written as a word, not a number
    }

    // ---- the palette files ----

    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static string Themes
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "VatisSync.sln"))) dir = dir.Parent;
            Assert.NotNull(dir);
            return Path.Combine(dir!.FullName, "src", "VatisSync", "Themes");
        }
    }

    private static Dictionary<string, string> Palette(string file) =>
        XDocument.Load(Path.Combine(Themes, file)).Descendants()
            .Where(e => e.Name.LocalName == "SolidColorBrush")
            .ToDictionary(e => (string)e.Attribute(X + "Key")!, e => (string)e.Attribute("Color")!);

    [Fact]
    public void All_three_palettes_define_exactly_the_same_colors()
    {
        var light = Palette("Palette.Light.xaml").Keys.Order().ToList();
        var dark = Palette("Palette.Dark.xaml").Keys.Order().ToList();
        var highContrast = Palette("Palette.HighContrast.xaml").Keys.Order().ToList();

        Assert.NotEmpty(light);
        Assert.Equal(light, dark);          // a color added to one and forgotten in another would show as Windows' default
        Assert.Equal(light, highContrast);
    }

    // The pairs that actually appear in the app: text on its backgrounds (4.5:1), and marks and outlines (3:1).
    private static readonly string[] Surfaces = ["WindowBackground", "PageBackground", "Surface", "SidebarBackground"];
    private static readonly string[] Everywhere = [.. Surfaces, "AccentWash", "CodeBackground"];

    private static IEnumerable<(string Fg, string Bg, double Need)> Pairs()
    {
        IEnumerable<(string, string, double)> Text(string fg, params string[] bgs) => bgs.Select(bg => (fg, bg, 4.5));
        IEnumerable<(string, string, double)> Mark(string fg, params string[] bgs) => bgs.Select(bg => (fg, bg, 3.0));

        return
        [
            .. Text("TextPrimary", [.. Surfaces, "ControlBackground", "TabBackground", "AccentWash", "AccentWashHover", "AccentWashPressed", "CodeBackground"]),
            .. Text("TextStrong", "CodeBackground", "SuccessWash", "WarningWash", "ErrorWash", "Surface", "WindowBackground"),
            .. Text("TextSecondary", [.. Surfaces, "ControlBackground", "TabBackground", "AccentWash", "AccentWashHover", "CodeBackground"]),
            .. Text("GlyphBrush", [.. Surfaces, "ControlBackground", "AccentWashHover"]),
            .. Text("OnFill", "Accent", "AccentHover", "AccentDark", "SuccessFill", "DangerFill"),
            .. Text("OnAccentWash", "AccentWash"),
            .. Text("AccentText", Everywhere),
            .. Text("SuccessText", [.. Everywhere, "SuccessWash"]),
            .. Text("WarningText", [.. Everywhere, "WarningWash"]),
            .. Text("ErrorText", [.. Everywhere, "ErrorWash"]),
            .. Mark("Accent", Surfaces),
            .. Mark("ControlBorderStrong", "WindowBackground", "Surface", "ControlBackground"),
            .. Mark("SuccessMark", Surfaces),
            .. Mark("WarningMark", [.. Surfaces, "WarningWash"]),
            .. Mark("ErrorMark", [.. Surfaces, "ErrorWash"]),
        ];
    }

    private static double Luminance(string hex)
    {
        double Channel(int at)
        {
            var c = int.Parse(hex.AsSpan(at, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }
        var h = hex.TrimStart('#');
        hex = h.Length == 8 ? h[2..] : h; // #AARRGGBB -> RRGGBB
        return 0.2126 * Channel(0) + 0.7152 * Channel(2) + 0.0722 * Channel(4);
    }

    private static double Contrast(string a, string b)
    {
        var (hi, lo) = (Math.Max(Luminance(a), Luminance(b)), Math.Min(Luminance(a), Luminance(b)));
        return (hi + 0.05) / (lo + 0.05);
    }

    [Fact]
    public void The_dark_palette_is_all_plain_colors_with_enough_contrast_wherever_it_is_used()
    {
        var dark = Palette("Palette.Dark.xaml");
        Assert.All(dark.Values, v => Assert.Matches("^#[0-9A-Fa-f]{8}$", v));

        var failures = Pairs()
            .Select(p => (p.Fg, p.Bg, p.Need, Got: Contrast(dark[p.Fg], dark[p.Bg])))
            .Where(p => p.Got < p.Need)
            .Select(p => $"{p.Fg} on {p.Bg}: {p.Got:0.00} (needs {p.Need})")
            .ToList();

        Assert.True(failures.Count == 0, "Too little contrast:\n" + string.Join("\n", failures));
    }

    [Fact]
    public void The_light_palette_has_enough_contrast_wherever_it_is_used()
    {
        // Light uses a few Windows system colors, which this test cannot read; the pairs involving them are skipped.
        var light = Palette("Palette.Light.xaml");
        var failures = Pairs()
            .Where(p => light[p.Fg].StartsWith('#') && light[p.Bg].StartsWith('#'))
            .Select(p => (p.Fg, p.Bg, p.Need, Got: Contrast(light[p.Fg], light[p.Bg])))
            .Where(p => p.Got < p.Need)
            .Select(p => $"{p.Fg} on {p.Bg}: {p.Got:0.00} (needs {p.Need})")
            .ToList();

        Assert.True(failures.Count == 0, "Too little contrast in the light palette:\n" + string.Join("\n", failures));
    }
}
