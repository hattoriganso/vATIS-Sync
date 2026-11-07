using System.Text.Json;
using VatisSync.Core;

namespace VatisSync.Tests;

/// <summary>
/// Tests against a real atis.info/api/all capture (KATL, KJFK, KLAX, KPHX, KMDW),
/// taken 2026-11-07 ~17:51Z. "Now" is pinned just after the capture so the staleness check passes.
/// </summary>
public class RealPayloadTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 7, 17, 55, 0, TimeSpan.Zero);

    private static readonly IReadOnlyList<DatisEntry> Entries = Load();

    private static List<DatisEntry> Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "atis-info-2026-11-07.json");
        return JsonSerializer.Deserialize<List<DatisEntry>>(File.ReadAllText(path),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }

    [Fact]
    public void Fixture_contains_the_five_airports()
    {
        var airports = Entries.Select(e => e.Airport).Distinct().Order().ToArray();
        Assert.Equal(["KATL", "KJFK", "KLAX", "KMDW", "KPHX"], airports);
    }

    [Theory]
    [InlineData("KATL_A")]
    [InlineData("KATL_D")]
    [InlineData("KJFK")]
    [InlineData("KLAX")]
    [InlineData("KMDW")]
    [InlineData("KPHX")]
    public void Every_station_formats_without_header_weather_or_tail(string station)
    {
        var r = DatisFormatter.Format(station, Entries, Now);
        Assert.True(r.IsAvailable, station);
        foreach (var field in new[] { r.AirportConditions, r.Notams })
        {
            Assert.DoesNotContain("ADVS YOU HAVE", field);
            Assert.DoesNotContain("1652Z", field);   // header time stripped
            Assert.DoesNotContain("RMK AO2", field); // METAR sentence stripped (vATIS supplies weather)
            Assert.DoesNotContain("  ", field);      // whitespace normalized within each field
        }
    }

    [Fact]
    public void Atlanta_departure_splits_at_existing_NOTAMS_marker()
    {
        var r = DatisFormatter.Format("KATL_D", Entries, Now);
        Assert.StartsWith("SIMUL DEPS, DEPG RWYS, 26L, 27R.", r.AirportConditions);
        Assert.EndsWith("GROUND CONTROL WILL ASSIGN RUNWAY WITH TAXI INSTRUCTIONS.", r.AirportConditions.Trim());
        Assert.StartsWith("TWY R10 CLSD.", r.Notams);
        Assert.EndsWith("ALL RAMPS, TWYS AND RWYS.", r.Notams.Trim());
    }

    [Fact]
    public void Atlanta_arrival_and_departure_are_distinct()
    {
        var a = DatisFormatter.Format("KATL_A", Entries, Now);
        var d = DatisFormatter.Format("KATL_D", Entries, Now);
        Assert.StartsWith("SIMULTANEOUS APCHS IN USE", a.AirportConditions);
        Assert.NotEqual(a.AirportConditions, d.AirportConditions);
    }

    [Fact]
    public void Atlanta_combined_station_is_unavailable_because_only_arr_and_dep_exist()
    {
        Assert.False(DatisFormatter.Format("KATL", Entries, Now).IsAvailable);
    }

    [Fact]
    public void Midway_NOTICE_TO_AIRMEN_is_normalized_and_split()
    {
        var r = DatisFormatter.Format("KMDW", Entries, Now);
        Assert.Equal("ILS RWY 31R APCH IN USE. LNDG AND DEPG RWYS 31.", r.AirportConditions.Trim());
        Assert.StartsWith("TWY K CLSD BTN TWY, Y AND TWY F", r.Notams);
    }

    [Fact]
    public void Phoenix_NOTICES_TO_AIRMEN_is_normalized_and_split()
    {
        // Regression: upstream only recognized the singular "NOTICE TO AIRMEN.", so PHX's NOTAMs
        // stayed inside Airport Conditions.
        var r = DatisFormatter.Format("KPHX", Entries, Now);
        Assert.DoesNotContain("NOTICES TO AIRMEN", r.AirportConditions);
        Assert.EndsWith("DEPARTING RWY 7L.", r.AirportConditions.Trim());
        Assert.StartsWith("CLNC DLVY FREQ 133.7", r.Notams);
        Assert.EndsWith("READBACK HOLD SHORT INSTRUCTIONS.", r.Notams.Trim());
    }

    [Fact]
    public void VanNuys_NOTICE_TO_AIRMEN_without_a_period_still_splits()
    {
        // Regression from the all-airports sweep: "...34 L/R . TAXILANE A1 CLOSED. . NOTICE TO AIRMEN RWY 34L PAPI OTS ."
        var all = JsonSerializer.Deserialize<List<DatisEntry>>(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "atis-info-all-2026-11-07.json")),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        var now = new DateTimeOffset(2026, 11, 7, 17, 55, 0, TimeSpan.Zero);
        var r = DatisFormatter.Format("KVNY", all, now);
        Assert.DoesNotContain("NOTICE", r.AirportConditions);
        Assert.StartsWith("RWY 34L PAPI OTS", r.Notams);
    }

    [Fact]
    public void Jfk_has_no_notams_section()
    {
        var r = DatisFormatter.Format("KJFK", Entries, Now);
        Assert.Equal("", r.Notams);
        Assert.StartsWith("APPROACH IN USE ILS RY 31R", r.AirportConditions);
    }

    [Fact]
    public void Lax_double_commas_and_spacing_are_cleaned()
    {
        // Source has "BTN TWY, C7 AND TWY C10 , , TWY B6" and "PUSH OR  TAXI".
        var r = DatisFormatter.Format("KLAX", Entries, Now);
        Assert.DoesNotContain(", ,", r.AirportConditions);
        Assert.DoesNotContain(" , ", r.AirportConditions);
        Assert.Contains("PUSH OR TAXI", r.AirportConditions);
    }

    [Fact]
    public void All_stations_become_unavailable_when_data_is_over_an_hour_and_three_quarters_old()
    {
        var later = Now.AddHours(2);
        foreach (var s in new[] { "KATL_A", "KATL_D", "KJFK", "KLAX", "KMDW", "KPHX" })
            Assert.False(DatisFormatter.Format(s, Entries, later).IsAvailable, s);
    }

    [Fact]
    public void Real_timestamps_with_seven_fraction_digits_parse()
    {
        // KATL timestamps look like 2026-11-07T17:50:41.2437236Z (7 digits).
        Assert.True(DatisFormatter.Format("KATL_D", Entries, Now).IsAvailable);
        Assert.False(DatisFormatter.Format("KATL_D", Entries, Now.AddMinutes(110)).IsAvailable);
    }
}
