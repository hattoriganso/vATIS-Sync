using System.Text.Json;
using VatisSync.Core;

namespace VatisSync.Tests;

/// <summary>
/// Runs the formatter over every entry in a full atis.info/api/all capture (88 entries, 2026-11-07)
/// and asserts invariants rather than exact text. A failure names the offending airport,
/// which is how formatting gaps like KPHX's "NOTICES TO AIRMEN." get found.
/// </summary>
public class AllAirportsSweepTests
{
    private static readonly List<DatisEntry> Entries = Load();

    // Pin "now" to the newest entry so staleness doesn't depend on when the test runs.
    private static readonly DateTimeOffset Now = Entries
        .Select(e => DateTimeOffset.Parse(e.UpdatedAt![..26], null,
            System.Globalization.DateTimeStyles.AssumeUniversal))
        .Max().AddMinutes(1);

    private static List<DatisEntry> Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "atis-info-all-2026-11-07.json");
        return JsonSerializer.Deserialize<List<DatisEntry>>(File.ReadAllText(path),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }

    private static string StationFor(DatisEntry e) => e.Type switch
    {
        "arr" => e.Airport + "_A",
        "dep" => e.Airport + "_D",
        _ => e.Airport,
    };

    private static string Label(DatisEntry e) => $"{e.Airport}/{e.Type}";

    [Fact]
    public void Fixture_is_the_full_payload()
    {
        Assert.True(Entries.Count >= 80, $"only {Entries.Count} entries");
    }

    [Fact]
    public void Every_entry_formats_without_throwing_and_keeps_its_invariants()
    {
        var problems = new List<string>();
        var formatted = 0;

        foreach (var e in Entries)
        {
            FormattedAtis r;
            try { r = DatisFormatter.Format(StationFor(e), Entries, Now); }
            catch (Exception ex) { problems.Add($"{Label(e)}: threw {ex.GetType().Name}: {ex.Message}"); continue; }

            if (!r.IsAvailable) continue; // empty/stale source data is legitimately unavailable
            formatted++;

            foreach (var (name, field) in new[] { ("conditions", r.AirportConditions), ("notams", r.Notams) })
            {
                void Check(bool ok, string why) { if (!ok) problems.Add($"{Label(e)} [{name}]: {why}"); }

                Check(!field.Contains("ADVS YOU HAVE"), "tail 'ADVS YOU HAVE' left in");
                Check(!field.Contains("  "), "double space");
                Check(!field.Contains(", ,"), "', ,'");
                Check(!field.Contains("&amp;"), "&amp; left in");
                Check(!field.Contains("NOTICE TO AIR") && !field.Contains("NOTICES TO AIR"),
                    "NOTICE(S) TO AIR… wording not normalized");
                // The split is on the FIRST marker, so conditions can never contain one. Notams may
                // legitimately hold "CHECK NOTAMS..." (the 'NOTAMS. ' rule rewrites it; same as upstream).
                if (name == "conditions")
                    Check(!field.Contains("NOTAMS... "), "NOTAMS marker left in conditions");
            }
        }

        Assert.True(formatted > 0, "nothing was formatted");
        Assert.True(problems.Count == 0,
            $"{problems.Count} problem(s) across {formatted} formatted entries:\n" + string.Join("\n", problems));
    }

    [Fact]
    public void Entries_with_a_notice_phrase_get_a_notams_section()
    {
        // If the raw text announces notices to airmen, the NOTAMs field should be populated.
        var problems = Entries
            .Where(e => e.Datis.Contains("NOTICE") && e.Datis.Contains("AIRMEN"))
            .Where(e => DatisFormatter.Format(StationFor(e), Entries, Now) is { IsAvailable: true, Notams: "" })
            .Select(Label)
            .ToList();
        Assert.True(problems.Count == 0, "NOTICE…AIRMEN present but no NOTAMs split for: " + string.Join(", ", problems));
    }
}
