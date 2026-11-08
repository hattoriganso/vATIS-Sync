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
}
