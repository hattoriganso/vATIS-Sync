using VatisSync.Core;

namespace VatisSync.Tests;

public class AppSettingsTests : TempDirTests
{
    private string SettingsPath => Path.Combine(Dir, "sub", "settings.json");

    [Fact]
    public void Missing_file_gives_defaults()
    {
        var s = new SettingsStore(SettingsPath).Load();
        Assert.Equal(5, s.IntervalMinutes);
        Assert.True(s.CloseToTray);
        Assert.Empty(s.Airports);
    }

    [Fact]
    public void Saved_settings_round_trip()
    {
        var store = new SettingsStore(SettingsPath);
        Assert.True(store.Save(new AppSettings
        {
            IntervalMinutes = 10, Airports = ["KSFO", "KOAK"], DisabledRules = ["drone-laser-reports"],
            StartWithWindows = true, StartMinimized = true, CloseToTray = false,
        }));

        var s = store.Load();
        Assert.Equal(10, s.IntervalMinutes);
        Assert.Equal(["KSFO", "KOAK"], s.Airports);
        Assert.Equal(["drone-laser-reports"], s.DisabledRules);
        Assert.True(s.StartWithWindows);
        Assert.False(s.CloseToTray);
        Assert.False(File.Exists(SettingsPath + ".tmp"));
    }

    [Fact]
    public void Corrupt_file_gives_defaults_and_one_warning()
    {
        var path = Write("settings.json", "{ not json");
        var store = new SettingsStore(path);
        var warnings = new List<string>();
        store.Warning += warnings.Add;

        Assert.Equal(5, store.Load().IntervalMinutes);
        Assert.Contains(path, Assert.Single(warnings));
    }

    [Fact]
    public void Unknown_keys_and_comments_are_tolerated_and_partial_files_fill_in_defaults()
    {
        var path = Write("settings.json", "{ // mine\n \"intervalMinutes\": 15, \"futureThing\": 1, }");
        var s = new SettingsStore(path).Load();
        Assert.Equal(15, s.IntervalMinutes);
        Assert.Equal(4, s.MaxConnections);
    }

    [Fact]
    public void Out_of_range_values_are_clamped_and_airports_cleaned()
    {
        var s = new AppSettings
        {
            IntervalMinutes = 0, MaxConnections = 99,
            Airports = [" ksfo ", "KSFO", "NOPE12", "", "koak"],
        }.Normalized();

        Assert.Equal(1, s.IntervalMinutes);
        Assert.Equal(4, s.MaxConnections);
        Assert.Equal(["KSFO", "KOAK"], s.Airports);
        Assert.Equal(60, new AppSettings { IntervalMinutes = 500 }.Normalized().IntervalMinutes);
    }

    [Fact]
    public void Converts_to_sync_options()
    {
        var o = new AppSettings
        {
            IntervalMinutes = 7, Airports = ["KSFO"], DisabledRules = ["x"], LaunchVatis = false,
            AutoConnectActivePosition = false,
        }.ToSyncOptions();

        Assert.Equal(TimeSpan.FromMinutes(7), o.Interval);
        Assert.Equal(["KSFO"], o.Airports);
        Assert.Equal(["x"], o.DisabledRules);
        Assert.False(o.LaunchVatis);
        Assert.False(o.AutoConnectActivePosition);
    }

    [Fact]
    public void Unwritable_location_warns_and_returns_false()
    {
        // A file where the directory should be makes CreateDirectory fail.
        var blocker = Write("blocker", "x");
        var store = new SettingsStore(Path.Combine(blocker, "settings.json"));
        var warnings = new List<string>();
        store.Warning += warnings.Add;

        Assert.False(store.Save(new AppSettings()));
        Assert.Single(warnings);
    }
}
