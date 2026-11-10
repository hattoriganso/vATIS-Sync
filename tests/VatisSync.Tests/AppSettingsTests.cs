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
            StartWithWindows = true, StartMinimized = true, CloseToTray = false, TrayHintShown = true,
        }));

        var s = store.Load();
        Assert.Equal(10, s.IntervalMinutes);
        Assert.Equal(["KSFO", "KOAK"], s.Airports);
        Assert.Equal(["drone-laser-reports"], s.DisabledRules);
        Assert.True(s.StartWithWindows);
        Assert.False(s.CloseToTray);
        Assert.True(s.TrayHintShown);
        Assert.False(new AppSettings().TrayHintShown);
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
    public void The_detailed_log_is_on_by_default_and_the_choice_is_remembered()
    {
        Assert.True(new AppSettings().DetailedLog);
        Assert.True(new SettingsStore(Write("old.json", "{ \"intervalMinutes\": 7 }")).Load().DetailedLog); // a file from before it existed

        var store = new SettingsStore(SettingsPath);
        store.Save(new AppSettings { DetailedLog = false });
        Assert.False(store.Load().DetailedLog);
    }

    [Theory]
    [InlineData("Always", ApprovalMode.RunwayChanges)] // the removed "ask before any change" mode of older versions
    [InlineData("RunwayChanges", ApprovalMode.RunwayChanges)]
    [InlineData("Never", ApprovalMode.Never)]
    [InlineData("something-new", ApprovalMode.RunwayChanges)]
    public void Approval_mode_in_an_older_or_odd_file_still_loads_and_keeps_the_other_settings(string mode, ApprovalMode expected)
    {
        var path = Write("settings.json", $"{{ \"approval\": \"{mode}\", \"intervalMinutes\": 15 }}");
        var loaded = new SettingsStore(path).Load();
        Assert.Equal(expected, loaded.Approval);
        Assert.Equal(15, loaded.IntervalMinutes); // nothing else is lost
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
            ConnectProfileStations = false,
        }.ToSyncOptions();

        Assert.Equal(TimeSpan.FromMinutes(7), o.Interval);
        Assert.Equal(["KSFO"], o.Airports);
        Assert.Equal(["x"], o.DisabledRules);
        Assert.False(o.LaunchVatis);
        Assert.False(o.AutoConnectActivePosition);
        Assert.False(o.ConnectProfileStations);
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

    [Fact]
    public void Frequency_settings_round_trip_as_readable_json()
    {
        var store = new SettingsStore(SettingsPath);
        store.Save(new AppSettings
        {
            Frequencies = new()
            {
                [FrequencyRole.Ground] = new(FrequencyMode.Override, " 121.900 "),
                [FrequencyRole.Ramp] = new(FrequencyMode.Remove),
                [FrequencyRole.Tower] = new(FrequencyMode.Off), // Off is the default and is not stored
            },
        });

        var json = File.ReadAllText(SettingsPath);
        Assert.Contains("\"Ground\"", json);
        Assert.Contains("\"Override\"", json);

        var s = store.Load();
        Assert.Equal(new FrequencySetting(FrequencyMode.Override, "121.900"), s.Frequencies[FrequencyRole.Ground]);
        Assert.Equal(FrequencyMode.Remove, s.Frequencies[FrequencyRole.Ramp].Mode);
        Assert.False(s.Frequencies.ContainsKey(FrequencyRole.Tower));
    }

    [Fact]
    public void An_override_without_a_valid_frequency_is_kept_in_the_file_but_acts_as_off()
    {
        var s = new AppSettings
        {
            Frequencies = new()
            {
                [FrequencyRole.Ground] = new(FrequencyMode.Override, "12"),
                [FrequencyRole.Tower] = new(FrequencyMode.Override, "118.5"),
            },
        };

        Assert.Equal(FrequencyMode.Override, s.Normalized().Frequencies[FrequencyRole.Ground].Mode);
        var options = s.ToSyncOptions().Frequencies;
        Assert.Equal(FrequencyMode.Off, options[FrequencyRole.Ground].Mode);
        Assert.Equal("118.5", options[FrequencyRole.Tower].Frequency);
    }

    [Fact]
    public void Missing_or_unknown_frequency_entries_are_tolerated()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        File.WriteAllText(SettingsPath, "{ \"frequencies\": { \"ground\": { \"mode\": \"remove\" } } }");
        Assert.Equal(FrequencyMode.Remove, new SettingsStore(SettingsPath).Load().Frequencies[FrequencyRole.Ground].Mode);
    }

    [Fact]
    public void Airport_overrides_and_known_airports_round_trip()
    {
        var store = new SettingsStore(SettingsPath);
        store.Save(new AppSettings
        {
            KnownAirports = ["ksfo", "KOAK", "bad"],
            AirportOverrides = new()
            {
                ["ksfo"] = new AirportSettings
                {
                    UseDefaultFrequencies = false,
                    Frequencies = new() { [FrequencyRole.Ground] = new(FrequencyMode.Override, "121.9") },
                },
                ["KOAK"] = new AirportSettings(), // follows the defaults everywhere: not worth storing
                ["toolong"] = new AirportSettings { UseDefaultRules = false },
            },
        });

        var s = store.Load();
        Assert.Equal(["KSFO", "KOAK"], s.KnownAirports);
        var ksfo = Assert.Single(s.AirportOverrides);
        Assert.Equal("KSFO", ksfo.Key);
        Assert.False(ksfo.Value.UseDefaultFrequencies);
        Assert.True(ksfo.Value.UseDefaultRules);
        Assert.Equal("121.9", ksfo.Value.Frequencies[FrequencyRole.Ground].Frequency);
    }

    [Fact]
    public void A_settings_file_from_before_airport_overrides_still_loads()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        File.WriteAllText(SettingsPath, "{ \"intervalMinutes\": 7, \"disabledRules\": [\"drone-laser-reports\"] }");
        var s = new SettingsStore(SettingsPath).Load();
        Assert.Equal(7, s.IntervalMinutes);
        Assert.Empty(s.AirportOverrides);
        Assert.Empty(s.KnownAirports);
    }

    [Fact]
    public void Each_section_of_an_airport_follows_the_defaults_or_its_own_copy()
    {
        var defaults = new AppSettings
        {
            DisabledRules = ["drone-laser-reports"],
            Frequencies = new() { [FrequencyRole.Ground] = new(FrequencyMode.Remove) },
            AirportOverrides = new()
            {
                ["KSFO"] = new AirportSettings
                {
                    UseDefaultFrequencies = false,
                    Frequencies = new() { [FrequencyRole.Tower] = new(FrequencyMode.Override, "118.5") },
                },
                ["KOAK"] = new AirportSettings { UseDefaultRules = false, DisabledRules = [] },
            },
        };

        var o = defaults.ToSyncOptions().PerAirport;
        Assert.Equal(["KOAK", "KSFO"], o.Keys.Order());

        // KSFO: own frequencies (the default Ground removal does not apply), default rules.
        Assert.Equal(["drone-laser-reports"], o["KSFO"].DisabledRules);
        Assert.Equal([FrequencyRole.Tower], o["KSFO"].Frequencies.Keys);

        // KOAK: own rules (all on), default frequencies.
        Assert.Empty(o["KOAK"].DisabledRules);
        Assert.Equal(FrequencyMode.Remove, o["KOAK"].Frequencies[FrequencyRole.Ground].Mode);

        // Other airports are absent from PerAirport and use the defaults in SyncOptions.
        Assert.Equal(["drone-laser-reports"], defaults.ToSyncOptions().DisabledRules);
    }
}
