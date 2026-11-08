using System.Text.Json;
using System.Text.RegularExpressions;
using VatisSync.Core;
using Xunit.Abstractions;

namespace VatisSync.Tests;

public class FrequencyDetectorTests(ITestOutputHelper output)
{
    private static FrequencySetting Remove => new(FrequencyMode.Remove);
    private static FrequencySetting Override(string? f) => new(FrequencyMode.Override, f);

    private static Dictionary<FrequencyRole, FrequencySetting> All(FrequencySetting s) =>
        Enum.GetValues<FrequencyRole>().ToDictionary(r => r, _ => s);

    private static Dictionary<FrequencyRole, FrequencySetting> One(FrequencyRole r, FrequencySetting s) => new() { [r] = s };

    private static string Run(string text, Dictionary<FrequencyRole, FrequencySetting> policy) =>
        Regex.Replace(FrequencyDetector.Apply(text, policy), @"\s+", " ").Trim();

    // ---- detection ----

    [Theory]
    [InlineData("CLEARANCE FREQUENCY IS 121.9.", FrequencyRole.ClearanceDelivery, "121.9")]
    [InlineData("CTC GND ON 121.9 FOR CLNC AND TAXI INSTRUCTIONS.", FrequencyRole.Ground, "121.9")]
    [InlineData("WHEN READY TO TAXI CONTACT GND METERING ON FREQ 121.67.", FrequencyRole.Metering, "121.67")]
    [InlineData("FOR PUSHBACK CONTACT RAMP CONTROL ON 130.87.", FrequencyRole.Ramp, "130.87")]
    [InlineData("HELICOPTOR CONTROL OPEN ON 119.9.", FrequencyRole.Helicopter, "119.9")]
    [InlineData("ALL ARRS CTC APCH 124.0.", FrequencyRole.Approach, "124.0")]
    [InlineData("CLASS C FREQ IS 132.82.", FrequencyRole.Approach, "132.82")]
    [InlineData("DEPT FREQ IS 119.35.", FrequencyRole.Departure, "119.35")]
    [InlineData("TWR FREQ ALL RWYS 123.77.", FrequencyRole.Tower, "123.77")]
    [InlineData("CTC 121.7 FOR CLNC DLVRY.", FrequencyRole.ClearanceDelivery, "121.7")] // role only in the tail
    public void Detects_role_and_frequency(string text, FrequencyRole role, string freq)
    {
        var m = Assert.Single(FrequencyDetector.Detect(text));
        Assert.Equal([role], m.Roles);
        Assert.Equal(freq, m.Frequency);
        Assert.False(m.Ignored);
    }

    [Fact]
    public void Spelled_out_frequency_is_converted_and_the_whole_phrase_is_the_frequency()
    {
        var text = "ALL ACFT CTC METERING ON ONE THREE THREE POINT FIVE SEVEN WHEN READY TO PUSHBACK OR TAXI.";
        var m = Assert.Single(FrequencyDetector.Detect(text));
        Assert.Equal("133.57", m.Frequency);
        Assert.Equal("ONE THREE THREE POINT FIVE SEVEN", text[m.FreqStart..m.FreqEnd]);
        Assert.Equal([FrequencyRole.Metering], m.Roles);
    }

    [Fact]
    public void Departing_and_departures_are_not_the_departure_position()
    {
        var m = Assert.Single(FrequencyDetector.Detect("DEPG RWY 28C, TWR FREQ 128.3."));
        Assert.Equal([FrequencyRole.Tower], m.Roles);
        m = Assert.Single(FrequencyDetector.Detect("ALL VFR DEPARTURES CONTACT CLEARANCE DELIVERY ON 128.05."));
        Assert.Equal([FrequencyRole.ClearanceDelivery], m.Roles);
    }

    [Theory]
    [InlineData("DEPARTURE ATIS 118.125.")]
    [InlineData("NOTAM ATIS IN USE ON FREQUENCY 126.225.")]
    [InlineData("HAZ WX INFO FOR TEB AREA AVBL FM MIV AFSS ON 122.2.")]
    public void Atis_and_flight_service_frequencies_are_ignored(string text)
    {
        Assert.True(Assert.Single(FrequencyDetector.Detect(text)).Ignored);
        Assert.Equal(text, FrequencyDetector.Apply(text, All(Remove)));
    }

    [Fact]
    public void Verify_atis_with_clearance_is_a_clearance_frequency()
    {
        var m = Assert.Single(FrequencyDetector.Detect("VERIFY SQK AND ATIS WITH CLNC ON FREQ 128.05."));
        Assert.False(m.Ignored);
        Assert.Equal([FrequencyRole.ClearanceDelivery], m.Roles);
    }

    [Theory]
    [InlineData("WIND 11010KT 10SM SCT080 A3011.")]
    [InlineData("ILS RWY 31R FREQ 110.3.")] // navaid band, not a comm frequency
    [InlineData("CONTACT CD 10 MIN PRIOR TO PUSH.")]
    [InlineData("ALTIMETER 30.12 TEMP 118 DEWPOINT 12.")]
    public void Non_frequencies_are_not_detected(string text) => Assert.Empty(FrequencyDetector.Detect(text));

    [Fact]
    public void Several_pairs_in_one_clause_are_separate_mentions()
    {
        var mentions = FrequencyDetector.Detect("A/S 1 AND 3 AND GA CTC GC 121.8 A/S 2 AND 4 CTC GC 126.4.");
        Assert.Equal(["121.8", "126.4"], mentions.Select(m => m.Frequency));
        Assert.All(mentions, m => Assert.Equal([FrequencyRole.Ground], m.Roles));
    }

    [Fact]
    public void A_pair_without_its_own_role_inherits_the_previous_one()
    {
        var mentions = FrequencyDetector.Detect("RAMP CONTROL OPEN FREQ NORTH 128.975 SOUTH 130.825.");
        Assert.Equal(2, mentions.Count);
        Assert.All(mentions, m => Assert.Equal([FrequencyRole.Ramp], m.Roles));
    }

    // ---- applying ----

    [Fact]
    public void Off_and_missing_roles_change_nothing()
    {
        const string text = "CTC GND ON 121.9 FOR TAXI. CONTACT TOWER 118.3.";
        Assert.Equal(text, FrequencyDetector.Apply(text, One(FrequencyRole.Ground, new FrequencySetting())));
        Assert.Equal(text, FrequencyDetector.Apply(text, new Dictionary<FrequencyRole, FrequencySetting>()));
        Assert.Equal(text, FrequencyDetector.Apply(text, null));
    }

    [Fact]
    public void Remove_takes_the_whole_instruction_including_its_purpose()
    {
        Assert.Equal("A. B.", Run("A. CTC GND ON 121.9 FOR CLNC AND TAXI INSTRUCTIONS. B.", One(FrequencyRole.Ground, Remove)));
        Assert.Equal("A. B.", Run("A. CTC GROUND 118.075 FOR CLEARANCE. B.", One(FrequencyRole.Ground, Remove)));
        Assert.Equal("A. B.", Run("A. ALL ACFT CTC METERING ON ONE THREE THREE POINT FIVE SEVEN WHEN READY TO PUSHBACK OR TAXI. B.",
            One(FrequencyRole.Metering, Remove)));
    }

    [Fact]
    public void Remove_stops_at_the_clause_and_tidies_the_comma()
    {
        Assert.Equal("DEPG RWY 28C.", Run("DEPG RWY 28C, TWR FREQ 128.3.", One(FrequencyRole.Tower, Remove)));
        // The neighbouring items after the comma are not touched.
        Assert.Equal("INT, E13 INT RWSL OTS.",
            Run("CLNC DLVY FREQ 133.7, INT, E13 INT RWSL OTS.", One(FrequencyRole.ClearanceDelivery, Remove)));
    }

    [Fact]
    public void Remove_handles_several_pairs_and_runway_lists()
    {
        Assert.Equal("A. B.", Run("A. TWR FREQ FOR RWY 10L IS 119.3 TWR FREQ FOR RWY 10R IS 120.2. B.",
            One(FrequencyRole.Tower, Remove)));
        Assert.Equal("DEPG RWYS 8L, 8R. NOTAMS.",
            Run("DEPG RWYS 8L, 8R. RWY 8L, RWY 8R FREQ 118.3 RWY 9, RWY 12 FREQ 123.9. NOTAMS.",
                One(FrequencyRole.Other, Remove)));
    }

    [Fact]
    public void Remove_one_pair_keeps_the_other_when_roles_differ()
    {
        Assert.Equal("A/S 2 AND 4 CTC TWR 126.4.",
            Run("A/S 1 AND 3 CTC GC 121.8 A/S 2 AND 4 CTC TWR 126.4.", One(FrequencyRole.Ground, Remove)));
    }

    [Fact]
    public void Override_replaces_only_the_frequency()
    {
        Assert.Equal("CTC GND ON 121.9 FOR TAXI.",
            Run("CTC GND ON 118.075 FOR TAXI.", One(FrequencyRole.Ground, Override("121.9"))));
        Assert.Equal("ALL ACFT CTC METERING ON 121.65 WHEN READY TO PUSHBACK.",
            Run("ALL ACFT CTC METERING ON ONE THREE THREE POINT FIVE SEVEN WHEN READY TO PUSHBACK.",
                One(FrequencyRole.Metering, Override("121.65"))));
    }

    [Fact]
    public void Several_roles_in_one_instruction_change_only_when_all_agree()
    {
        const string text = "CD AND GC COMBINED ON 123.9.";
        var cd = FrequencyRole.ClearanceDelivery; var gc = FrequencyRole.Ground;

        Assert.Equal(text, Run(text, One(cd, Remove))); // ground is still real
        Assert.Equal("", Run(text, new() { [cd] = Remove, [gc] = Remove }));
        Assert.Equal(text, Run(text, new() { [cd] = Override("121.7"), [gc] = Override("121.9") }));
        Assert.Equal("CD AND GC COMBINED ON 121.7.", Run(text, new() { [cd] = Override("121.7"), [gc] = Override("121.7") }));
    }

    [Fact]
    public void Override_without_a_valid_frequency_acts_as_off()
    {
        const string text = "CTC GND ON 118.075.";
        foreach (var bad in new[] { null, "", "abc", "99.5", "121", "121.9999", "150.0" })
            Assert.Equal(text, FrequencyDetector.Apply(text, One(FrequencyRole.Ground, Override(bad))));
    }

    [Theory]
    [InlineData("121.9", true)] [InlineData("118.0", true)] [InlineData("136.975", true)]
    [InlineData("117.95", false)] [InlineData("137.0", false)] [InlineData("121,9", false)] [InlineData(null, false)]
    public void Frequency_validation(string? value, bool valid) => Assert.Equal(valid, FrequencyDetector.IsValidFrequency(value));

    // ---- through the formatter, on the real capture ----

    private static readonly List<DatisEntry> Entries = JsonSerializer.Deserialize<List<DatisEntry>>(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "atis-info-all-2026-11-07.json")),
        new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

    private static readonly DateTimeOffset Now = Entries
        .Select(e => DateTimeOffset.Parse(e.UpdatedAt![..26], null, System.Globalization.DateTimeStyles.AssumeUniversal))
        .Max().AddMinutes(1);

    private static string Station(DatisEntry e) => e.Type switch
    { "arr" => e.Airport + "_A", "dep" => e.Airport + "_D", _ => e.Airport };

    private static int Count(string text, string part) => text.Split(part).Length - 1;

    private static string Text(FormattedAtis f) => (f.AirportConditions + " NOTAMS... " + f.Notams).Trim();

    [Fact]
    public void Real_capture_removal_leaves_clean_text_and_no_frequency_behind()
    {
        var changed = 0; var edits = new List<string>();
        foreach (var e in Entries)
        {
            var before = DatisFormatter.Format(Station(e), Entries, Now);
            var after = DatisFormatter.Format(Station(e), Entries, Now, frequencies: All(Remove));

            // Removal must not add stray punctuation (the source text itself has a few, such as KCHS's "PUSHBACK. .").
            foreach (var junk in new[] { "..", " ,", ", ,", " ." })
                Assert.True(Count(after.AirportConditions, junk) <= Count(before.AirportConditions, junk),
                    $"{Station(e)}: removal added '{junk}': {after.AirportConditions}");
            // Only ATIS and Flight Service frequencies may remain.
            var left = FrequencyDetector.Detect(Text(after)).Where(m => !m.Ignored).ToList();
            Assert.True(left.Count == 0, $"{Station(e)} still has {string.Join(", ", left.Select(m => m.Frequency))}: {Text(after)}");

            if (before == after) continue;
            changed++;
            edits.Add($"{Station(e)}\n  - {Text(before)}\n  + {Text(after)}");
        }
        output.WriteLine(string.Join("\n", edits));
        Assert.True(changed > 20, $"expected many airports to change, got {changed}");
    }

    [Fact]
    public void Real_capture_override_writes_the_chosen_frequency_everywhere()
    {
        foreach (var e in Entries)
        {
            var after = Text(DatisFormatter.Format(Station(e), Entries, Now, frequencies: All(Override("121.9"))));
            foreach (var m in FrequencyDetector.Detect(after).Where(m => !m.Ignored))
                Assert.Equal("121.9", m.Frequency);
        }
    }
}
