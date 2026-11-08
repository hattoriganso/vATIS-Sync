namespace VatisSync.Core;

/// <summary>Picks the airports out of the stations vATIS reports for the profile the user has open.</summary>
public static class ProfileAirports
{
    /// <summary>
    /// The distinct airports (ICAO, upper-case, alphabetical) that have at least one station with a D-ATIS preset.
    /// KMIA_A and KMIA_D are one airport.
    /// </summary>
    public static List<string> From(IEnumerable<VatisStation> stations) =>
        stations.Where(s => s.HasDatisPreset && s.Name.Length >= 4)
            .Select(s => s.Name[..4].ToUpperInvariant())
            .Distinct()
            .Order(StringComparer.Ordinal)
            .ToList();
}
