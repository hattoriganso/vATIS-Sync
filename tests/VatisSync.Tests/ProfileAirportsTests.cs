using VatisSync.Core;

namespace VatisSync.Tests;

public class ProfileAirportsTests
{
    private static VatisStation Station(string name, string type = "Combined", params string[] presets) =>
        new("id-" + name + type, name, type, presets.Length == 0 ? ["D-ATIS"] : presets);

    [Fact]
    public void Lists_each_airport_with_a_datis_station_once_in_alphabetical_order()
    {
        var airports = ProfileAirports.From([
            Station("KSFO"), Station("KMIA", "Arrival"), Station("KMIA", "Departure"), Station("kdfw"),
        ]);
        Assert.Equal(["KDFW", "KMIA", "KSFO"], airports);
    }

    [Fact]
    public void Stations_without_a_datis_preset_are_left_out()
    {
        var airports = ProfileAirports.From([
            Station("KSFO"), Station("KOAK", "Combined", "Manual"), Station("KSJC", "Combined", "Manual", "D-ATIS"),
        ]);
        Assert.Equal(["KSFO", "KSJC"], airports);
    }

    [Fact]
    public void An_empty_or_nameless_profile_gives_nothing()
    {
        Assert.Empty(ProfileAirports.From([]));
        Assert.Empty(ProfileAirports.From([Station("ABC")])); // not an ICAO
    }

    [Fact]
    public void Stations_without_the_datis_preset_are_reported_by_name()
    {
        var skipped = ProfileAirports.WithoutDatisPreset([
            Station("KSFO"),                                  // fine
            Station("KJFK", "Combined", "Manual"),             // has presets, but not D-ATIS
            new VatisStation("id-new", "KMIA", "Departure", []), // a new, empty station
            Station("KMIA", "Arrival"),                         // the arrival one is fine
            Station("D-ATIS-ish"),                              // not relevant: has the preset
            Station("ABC", "Combined", "Manual"),               // too short to be an airport
        ]);

        Assert.Equal(["KJFK", "KMIA_D"], skipped);
    }

    [Fact]
    public void The_preset_name_must_match_exactly()
    {
        var skipped = ProfileAirports.WithoutDatisPreset([
            Station("KAAA", "Combined", "d-atis"), Station("KBBB", "Combined", "DATIS"), Station("KCCC", "Combined", "D-ATIS")]);
        Assert.Equal(["KAAA", "KBBB"], skipped);
    }
}
