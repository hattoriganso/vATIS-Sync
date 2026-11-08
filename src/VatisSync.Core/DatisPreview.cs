using System.Text.RegularExpressions;

namespace VatisSync.Core;

public enum DiffKind
{
    /// <summary>In both the real-world text and the text for vATIS.</summary>
    Same,

    /// <summary>In the real-world text but taken out (or replaced) for vATIS.</summary>
    Removed,

    /// <summary>Not in the real-world text: put in by a replacement, such as a frequency you chose.</summary>
    Added,
}

/// <summary>A run of words that all have the same <see cref="DiffKind"/>.</summary>
public sealed record DiffSegment(string Text, DiffKind Kind);

/// <summary>What one station's D-ATIS looks like for vATIS, and how it differs from the real-world text.</summary>
/// <param name="Station">As vATIS shows it: "KSFO", "KMIA_A", "KMIA_D".</param>
/// <param name="Available">Real-world D-ATIS exists for it and is recent enough to use.</param>
/// <param name="Problem">When not available, why (ready to show the user).</param>
/// <param name="Letter">The ATIS letter in the real-world text, if it could be read.</param>
/// <param name="Issued">When atis.info last updated this D-ATIS.</param>
public sealed record StationPreview(
    string Station,
    bool Available,
    string? Problem,
    string? Letter,
    DateTimeOffset? Issued,
    string Conditions,
    string Notams,
    IReadOnlyList<DiffSegment> ConditionsDiff,
    IReadOnlyList<DiffSegment> NotamsDiff);

/// <param name="FetchProblem">Why the real-world data could not be downloaded this time (older data may still be shown).</param>
/// <param name="FromCache">The data is the last good copy, not a fresh download.</param>
/// <param name="FetchedAt">When the data was downloaded.</param>
public sealed record PreviewResult(
    IReadOnlyList<StationPreview> Stations, string? FetchProblem, bool FromCache, DateTimeOffset FetchedAt);

/// <summary>
/// Builds what an airport's ATIS would be, without talking to vATIS: the real-world D-ATIS (from atis.info, cached for a
/// minute) with the airport's rules and frequency settings applied, plus a comparison with the raw text. Read-only: it
/// writes nothing anywhere. vATIS contractions and text the user deleted by hand are not applied (they live in vATIS).
/// </summary>
public sealed class DatisPreviewBuilder(IDatisSource datis, IReplacementSource replacements, TimeProvider? time = null)
{
    /// <summary>How long a download is reused, so switching between airports does not download again each time.</summary>
    public static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(60);

    /// <summary>A failed download is retried sooner than a good one is replaced.</summary>
    public static readonly TimeSpan FailureCacheFor = TimeSpan.FromSeconds(10);

    private static readonly Regex Letter = new(@"\bINFO\s+([A-Z])\b", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DatisFetchResult? _fetch;
    private DateTimeOffset _fetchedAt;
    private DateTimeOffset _validUntil;

    /// <param name="stationKeys">The airport's stations as vATIS names them.</param>
    /// <param name="rules">The airport's rules and frequency settings, defaults already resolved.</param>
    /// <param name="forceRefresh">Download again even if a recent copy is held.</param>
    public async Task<PreviewResult> BuildAsync(
        IReadOnlyList<string> stationKeys, AirportRules rules, bool forceRefresh = false, CancellationToken ct = default)
    {
        var fetch = await GetFetchAsync(forceRefresh, ct).ConfigureAwait(false);
        var now = _time.GetUtcNow();

        var icaos = stationKeys.Where(k => k.Length >= 4).Select(k => k[..4]).Distinct().ToList();
        var local = await replacements.GetAsync(icaos, ct).ConfigureAwait(false);
        var builtIn = BuiltInRules.PatternsFor(rules.DisabledRules);

        var stations = new List<StationPreview>();
        foreach (var key in stationKeys.Where(k => k.Length >= 4).Distinct().Order(StringComparer.Ordinal))
        {
            var stationRules = new List<KeyValuePair<string, string>>(builtIn);
            if (local.TryGetValue(key[..4], out var airportRules)) stationRules.AddRange(airportRules);
            stations.Add(BuildStation(key, fetch.Entries, now, stationRules, rules.Frequencies));
        }
        return new PreviewResult(stations, fetch.Error, fetch.FromCache, _fetchedAt);
    }

    private static StationPreview BuildStation(
        string key, IReadOnlyList<DatisEntry> entries, DateTimeOffset now,
        IReadOnlyList<KeyValuePair<string, string>> rules, IReadOnlyDictionary<FrequencyRole, FrequencySetting> frequencies)
    {
        var icao = key[..4];
        var type = DatisFormatter.AtisTypeFor(key);
        var entry = entries.LastOrDefault(e => e.Airport == icao && e.Type == type);
        if (entry is null || string.IsNullOrEmpty(entry.Datis))
            return Unavailable(key, Messages.Get(Msg.SummaryNoData, new { station = key }), null, null);

        DateTimeOffset? issued = DatisFormatter.TryParseUpdatedAt(entry.UpdatedAt, out var parsed) ? parsed : null;
        var letter = Letter.Match(entry.Datis) is { Success: true } m ? m.Groups[1].Value : null;
        if (issued is { } at && (now - at).TotalHours > DatisFormatter.MaxAgeHours)
            return Unavailable(key, Messages.Get(Msg.SummaryTooOld, new { station = key }), letter, issued);

        var final = DatisFormatter.Format(key, entries, now, rules, contractions: null, frequencies);
        if (!final.IsAvailable) return Unavailable(key, Messages.Get(Msg.SummaryNoData, new { station = key }), letter, issued);

        var raw = DatisFormatter.Format(key, entries, now); // the same text with no rules and no frequency changes
        return new StationPreview(key, true, null, letter, issued, final.AirportConditions, final.Notams,
            WordDiff.Unified(raw.AirportConditions, final.AirportConditions),
            WordDiff.Unified(raw.Notams, final.Notams));
    }

    private static StationPreview Unavailable(string key, string problem, string? letter, DateTimeOffset? issued) =>
        new(key, false, problem, letter, issued, "", "", [], []);

    private async Task<DatisFetchResult> GetFetchAsync(bool force, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var now = _time.GetUtcNow();
            if (!force && _fetch is not null && now < _validUntil) return _fetch;

            var fetch = await datis.FetchAsync(ct).ConfigureAwait(false);
            _fetch = fetch;
            _fetchedAt = now;
            _validUntil = now + (fetch.Succeeded ? CacheFor : FailureCacheFor);
            return fetch;
        }
        finally
        {
            _gate.Release();
        }
    }
}

/// <summary>Wording for the Summary tab that depends on the data (ages and sources).</summary>
public static class PreviewText
{
    /// <summary>"just now", "12 minutes ago", "3 hours ago".</summary>
    public static string Age(DateTimeOffset issued, DateTimeOffset now)
    {
        var age = now - issued;
        if (age < TimeSpan.Zero) age = TimeSpan.Zero;
        if (age < TimeSpan.FromMinutes(1)) return Messages.Get(Msg.SummaryAgeNow);
        if (age < TimeSpan.FromHours(2)) return Messages.Get(Msg.SummaryAgeMinutes, new { count = (int)Math.Round(age.TotalMinutes) });
        return Messages.Get(Msg.SummaryAgeHours, new { count = (int)age.TotalHours });
    }

    /// <summary>"Information P, issued 12 minutes ago.", or null when it is not known when the D-ATIS was issued.</summary>
    public static string? Source(StationPreview station, DateTimeOffset now)
    {
        if (station.Issued is not { } issued) return null;
        var age = Age(issued, now);
        return station.Letter is { } letter
            ? Messages.Get(Msg.SummarySource, new { letter, age })
            : Messages.Get(Msg.SummarySourceNoLetter, new { age });
    }
}
