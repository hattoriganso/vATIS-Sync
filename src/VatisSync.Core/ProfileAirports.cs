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

    /// <summary>
    /// The stations that are in the profile but are skipped because they have no preset named exactly "D-ATIS" (for example a
    /// brand-new empty station), by station name as vATIS shows it ("KJFK", "KMIA_D"), alphabetical.
    /// </summary>
    public static List<string> WithoutDatisPreset(IEnumerable<VatisStation> stations) =>
        stations.Where(s => !s.HasDatisPreset && s.Name.Length >= 4)
            .Select(s => s.Key)
            .Distinct()
            .Order(StringComparer.Ordinal)
            .ToList();
}
