using VatisSync.Core;

namespace VatisSync.Tests;

public class AirportFileStoreTests : TempDirTests
{
    private string Folder => Path.Combine(Dir, "Airports");

    private AirportFileStore Store(List<string>? warnings = null)
    {
        var store = new AirportFileStore(Folder);
        if (warnings is not null) store.Warning += warnings.Add;
        return store;
    }

    private static AirportSettings Custom() => new()
    {
        UseDefaultRules = false,
        DisabledRules = ["drone-laser-reports"],
        UseDefaultFrequencies = false,
        Frequencies = new()
        {
            [FrequencyRole.Ground] = new(FrequencyMode.Override, "121.9"),
            [FrequencyRole.Tower] = new(FrequencyMode.Remove),
        },
    };

    [Fact]
    public void Settings_round_trip_through_the_airports_file()
    {
        var store = Store();
        Assert.True(store.Save("KSFO", Custom()));

        var loaded = Assert.IsType<AirportSettings>(store.Load("KSFO"));
        Assert.False(loaded.UseDefaultRules);
        Assert.Equal(["drone-laser-reports"], loaded.DisabledRules);
        Assert.False(loaded.UseDefaultFrequencies);
        Assert.Equal(new FrequencySetting(FrequencyMode.Override, "121.9"), loaded.Frequencies[FrequencyRole.Ground]);
        Assert.Equal(FrequencyMode.Remove, loaded.Frequencies[FrequencyRole.Tower].Mode);
        Assert.Equal(2, loaded.Frequencies.Count);
    }

    [Fact]
    public void The_file_is_named_for_the_airport_and_explains_itself_in_comments()
    {
        var store = Store();
        store.Save("ksfo", Custom()); // lower case is normalized

        var path = Path.Combine(Folder, "KSFO.toml");
        Assert.True(File.Exists(path));
        var text = File.ReadAllText(path);
        Assert.StartsWith("# vATIS-Sync settings for KSFO only.", text);
        Assert.Contains("# Rule names: flight-service-pointers", text);
        Assert.Contains("Ground = { mode = \"Override\", frequency = \"121.9\" }", text);
        Assert.Contains("Tower = { mode = \"Remove\" }", text);
    }

    [Fact]
    public void An_airport_that_follows_the_defaults_everywhere_has_no_file_and_loses_an_old_one()
    {
        var store = Store();
        Assert.True(store.Save("KSFO", new AirportSettings()));
        Assert.False(File.Exists(store.PathFor("KSFO")));
        Assert.Null(store.Load("KSFO"));

        store.Save("KSFO", Custom());
        Assert.True(File.Exists(store.PathFor("KSFO")));
        store.Save("KSFO", new AirportSettings());
        Assert.False(File.Exists(store.PathFor("KSFO")));
    }

    [Fact]
    public void Only_valid_airport_codes_are_used_as_file_names()
    {
        var store = Store();
        foreach (var bad in new[] { "../KSF", "..\\KSF", "KS", "KSFOX", "K S", "" })
        {
            Assert.False(store.Save(bad, Custom()));
            Assert.Null(store.Load(bad));
        }
        Assert.False(Directory.Exists(Folder) && Directory.EnumerateFileSystemEntries(Folder).Any());
        Assert.False(File.Exists(Path.Combine(Dir, "KSF.toml")));
    }

    [Fact]
    public void LoadAll_returns_every_airport_file_and_ignores_other_files()
    {
        var store = Store();
        store.Save("KSFO", Custom());
        store.Save("KOAK", new AirportSettings { UseDefaultRules = false, DisabledRules = ["flight-service-pointers"] });
        File.WriteAllText(Path.Combine(Folder, "notes.toml"), "use-default-rules = false");
        File.WriteAllText(Path.Combine(Folder, "KMIA.toml.broken"), "x");
        File.WriteAllText(Path.Combine(Folder, "KJFK.toml.tmp"), "x");

        var all = store.LoadAll();
        Assert.Equal(["KOAK", "KSFO"], all.Keys.Order());
        Assert.Equal(["flight-service-pointers"], all["KOAK"].DisabledRules);
    }

    [Fact]
    public void LoadAll_on_a_missing_folder_is_empty()
    {
        Assert.Empty(Store().LoadAll());
    }

    [Fact]
    public void A_hand_edited_file_with_comments_and_unknown_keys_is_understood()
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllText(Path.Combine(Folder, "KSFO.toml"), """
            # my notes about KSFO
            use-default-rules = false   # I want my own list here
            disabled-rules = ["drone-laser-reports", ""]
            favourite-colour = "blue"

            use-default-frequencies = false
            [frequencies]
            ground = { mode = "override", frequency = " 121.9 " }
            Tower = { mode = "Remove" }
            Ramp = { mode = "Sometimes" }       # not a mode: skipped
            Nonsense = { mode = "Remove" }      # not a position: skipped
            """);

        var s = Assert.IsType<AirportSettings>(Store().Load("KSFO"));
        Assert.False(s.UseDefaultRules);
        Assert.Equal(["drone-laser-reports"], s.DisabledRules);
        Assert.Equal(new FrequencySetting(FrequencyMode.Override, "121.9"), s.Frequencies[FrequencyRole.Ground]);
        Assert.Equal(FrequencyMode.Remove, s.Frequencies[FrequencyRole.Tower].Mode);
        Assert.Equal(2, s.Frequencies.Count);
    }

    [Fact]
    public void Missing_keys_mean_follow_the_defaults()
    {
        var s = AirportFileStore.Parse("# nothing here\n");
        Assert.True(s.UseDefaultRules);
        Assert.True(s.UseDefaultFrequencies);
        Assert.True(s.IsDefault);
    }

    [Fact]
    public void A_broken_file_is_set_aside_with_one_warning_and_not_overwritten_blindly()
    {
        Directory.CreateDirectory(Folder);
        var path = Path.Combine(Folder, "KSFO.toml");
        File.WriteAllText(path, "this is = = not toml");
        var warnings = new List<string>();
        var store = Store(warnings);

        Assert.Null(store.Load("KSFO"));

        Assert.Single(warnings);
        Assert.Contains("KSFO", warnings[0]);
        Assert.False(File.Exists(path));
        Assert.Equal("this is = = not toml", File.ReadAllText(path + ".broken")); // what the user wrote is kept

        // A later save writes a fresh file and does not touch the kept copy.
        store.Save("KSFO", Custom());
        Assert.True(File.Exists(path));
        Assert.True(File.Exists(path + ".broken"));
        Assert.Null(store.Load("KOAK")); // and other airports are unaffected
    }

    [Fact]
    public void An_unwritable_folder_warns_instead_of_throwing()
    {
        File.WriteAllText(Folder, "a file where the folder should be");
        var warnings = new List<string>();

        Assert.False(Store(warnings).Save("KSFO", Custom()));
        Assert.Single(warnings);
        Assert.Contains("KSFO", warnings[0]);
    }

    [Fact]
    public void Quotes_and_backslashes_in_values_survive_a_round_trip()
    {
        var store = Store();
        store.Save("KSFO", new AirportSettings { UseDefaultRules = false, DisabledRules = ["a \"quoted\" \\ rule"] });
        Assert.Equal(["a \"quoted\" \\ rule"], store.Load("KSFO")!.DisabledRules);
    }

    [Fact]
    public void ImportMissing_brings_old_settings_over_without_overwriting_or_storing_defaults()
    {
        var store = Store();
        store.Save("KSFO", new AirportSettings { UseDefaultRules = false, DisabledRules = ["flight-service-pointers"] }); // newer than the old settings

        var imported = store.ImportMissing(new Dictionary<string, AirportSettings>
        {
            ["KSFO"] = Custom(),              // already has a file: left alone
            ["KOAK"] = Custom(),              // imported
            ["KMIA"] = new AirportSettings(), // nothing to keep
        });

        Assert.Equal(1, imported);
        Assert.Equal(["flight-service-pointers"], store.Load("KSFO")!.DisabledRules);
        Assert.NotNull(store.Load("KOAK"));
        Assert.Null(store.Load("KMIA"));
    }

    [Fact]
    public void A_loaded_file_resolves_against_the_defaults_like_any_other_airport_settings()
    {
        var store = Store();
        store.Save("KSFO", new AirportSettings
        {
            UseDefaultFrequencies = false,
            Frequencies = new() { [FrequencyRole.Tower] = new(FrequencyMode.Override, "118.5") },
        });
        var defaults = new AppSettings { DisabledRules = ["drone-laser-reports"] };

        var resolved = store.Load("KSFO")!.Resolve(defaults);

        Assert.Equal(["drone-laser-reports"], resolved.DisabledRules); // rules: the defaults
        Assert.Equal("118.5", resolved.Frequencies[FrequencyRole.Tower].Frequency); // frequencies: its own
    }
}
