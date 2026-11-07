using VatisSync.Core;

namespace VatisSync.Tests;

/// <summary>[Fact] that only runs when VATIS_LIVE_TESTS=1 (it uses the network).</summary>
public sealed class LiveFactAttribute : FactAttribute
{
    public LiveFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("VATIS_LIVE_TESTS") != "1")
            Skip = "Set VATIS_LIVE_TESTS=1 to run tests against the live atis.info API.";
    }
}

public class LiveDatisTests
{
    /// <summary>
    /// Runs the same invariants as the all-airports sweep on today's real data, to catch new D-ATIS wording
    /// before users do. Failures name the airport, so the fix is usually one more case in DatisFormatter.
    /// </summary>
    [LiveFact]
    public async Task Live_data_satisfies_formatter_invariants()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        var result = await new DatisFetcher(http).FetchAsync();
        Assert.True(result.Succeeded, result.Error);
        Assert.NotEmpty(result.Entries);

        var now = DateTimeOffset.UtcNow;
        var problems = new List<string>();
        foreach (var e in result.Entries)
        {
            var station = e.Type switch { "arr" => e.Airport + "_A", "dep" => e.Airport + "_D", _ => e.Airport };
            var r = DatisFormatter.Format(station, result.Entries, now);
            if (!r.IsAvailable) continue;

            foreach (var field in new[] { r.AirportConditions, r.Notams })
            {
                if (field.Contains("ADVS YOU HAVE")) problems.Add($"{station}: tail left in");
                if (field.Contains("  ")) problems.Add($"{station}: double space");
                if (field.Contains("&amp;")) problems.Add($"{station}: &amp;");
                if (field.Contains("NOTICE TO AIR") || field.Contains("NOTICES TO AIR"))
                    problems.Add($"{station}: NOTICE wording not normalized");
            }
            if (e.Datis.Contains("NOTICE") && e.Datis.Contains("AIRMEN") && r.Notams == "")
                problems.Add($"{station}: NOTICE…AIRMEN present but no NOTAMs split");
        }
        Assert.True(problems.Count == 0, string.Join("\n", problems.Distinct()));
    }
}
