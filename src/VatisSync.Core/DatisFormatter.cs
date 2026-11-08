using System.Globalization;
using System.Text.RegularExpressions;

namespace VatisSync.Core;

/// <summary>One entry from https://atis.info/api/all.</summary>
public sealed record DatisEntry(string Airport, string Type, string Datis, string? UpdatedAt);

/// <summary>Result of formatting: text for the Airport Conditions and NOTAMs fields.</summary>
public sealed record FormattedAtis(string AirportConditions, string Notams)
{
    public const string NotAvailable = "D-ATIS NOT AVBL.";
    public static FormattedAtis Unavailable { get; } = new(NotAvailable, "");
    public bool IsAvailable => AirportConditions != NotAvailable;
}

/// <summary>
/// Port of get_datis() from vATISLoad.py. Pure: no network or vATIS access, so it is unit-testable.
/// </summary>
public static class DatisFormatter
{
    public const double MaxAgeHours = 1.75;

    /// <summary>Rules are user-editable regexes, so cap how long any one may run.</summary>
    private static readonly TimeSpan RuleTimeout = TimeSpan.FromSeconds(1);

    private static readonly Regex NoticeToAirmen = new(
        @"NOTICES? TO AIR ?(?:MEN|MISSIONS)(?:, NOTAMS)?\.? ", RegexOptions.Compiled);

    /// <summary>"KSFO_A" -> "arr", "KSFO_D" -> "dep", otherwise "combined".</summary>
    public static string AtisTypeFor(string station) =>
        station.Contains("_A") ? "arr" : station.Contains("_D") ? "dep" : "combined";

    /// <param name="station">Station name as built by the client, e.g. "KSFO" or "KSFO_A".</param>
    /// <param name="replacements">Ordered (regex, replacement) pairs. "%r" in a replacement is removed.</param>
    /// <param name="contractions">(text, "@VARIABLE") pairs, longest text first.</param>
    /// <param name="frequencies">What to do with the frequencies of each position; null or empty leaves them all alone.</param>
    public static FormattedAtis Format(
        string station,
        IReadOnlyList<DatisEntry>? entries,
        DateTimeOffset now,
        IReadOnlyList<KeyValuePair<string, string>>? replacements = null,
        IReadOnlyList<KeyValuePair<string, string>>? contractions = null,
        IReadOnlyDictionary<FrequencyRole, FrequencySetting>? frequencies = null)
    {
        if (entries is null || station.Length < 4) return FormattedAtis.Unavailable;

        var icao = station[..4];
        var type = AtisTypeFor(station);
        var datis = "";

        foreach (var e in entries)
        {
            if (e.Airport != icao || e.Type != type) continue;
            datis = e.Datis ?? "";
            // Ignore D-ATIS more than 1.75 hours old (unparseable timestamps are ignored, as in the original).
            if (TryParseUpdatedAt(e.UpdatedAt, out var updated) &&
                (now - updated).TotalHours > MaxAgeHours)
                return FormattedAtis.Unavailable;
        }

        if (datis.Length == 0) return FormattedAtis.Unavailable;

        // Strip beginning (first two sentences) and ending ("...ADVS YOU HAVE") D-ATIS text.
        datis = string.Join(". ", datis.Split(". ").Skip(2));
        datis = Regex.Replace(datis, " ...ADVS YOU HAVE.*", "", RegexOptions.Singleline);
        // Covers the upstream forms (NOTICE TO AIR MISSIONS[, NOTAMS]. / AIR MEN. / AIRMEN.) plus
        // variants seen in real feeds that upstream misses: "NOTICES TO AIRMEN." (KPHX) and a missing
        // period, "NOTICE TO AIRMEN RWY ..." (KVNY).
        datis = NoticeToAirmen.Replace(datis, "NOTAMS... ");
        datis = datis
            .Replace("NOTAMS. ", "NOTAMS... ")
            .Replace("NOTAM. ", "NOTAMS... ");

        if (replacements is not null)
        {
            foreach (var (pattern, replacement) in replacements)
            {
                var repl = ToDotNetReplacement(replacement.Replace("%r", ""));
                try { datis = Regex.Replace(datis, pattern + "[,.;]{0,2}", repl, RegexOptions.None, RuleTimeout); }
                catch (Exception ex) when (ex is ArgumentException or RegexMatchTimeoutException)
                {
                    // Bad or pathological rule: skip it rather than crash or hang the sync.
                }
            }
        }

        // After the rules, so a frequency inside text they already removed is not seen twice.
        datis = FrequencyDetector.Apply(datis, frequencies);

        datis = Regex.Replace(datis, @"\s+", " ").Trim();

        datis = datis.Replace("...", "/./").Replace("..", ".")
            .Replace("/./", "...").Replace("  ", " ").Replace(" . ", ". ")
            .Replace(", ,", ",").Replace(" ; ", "; ").Replace(" .,", " ,")
            .Replace(" , ", ", ").Replace("., ", ", ").Replace("&amp;", "&")
            .Replace(" ;.", ".").Replace(" ;,", ",");

        if (contractions is not null)
        {
            foreach (var (text, variable) in contractions)
            {
                if (text.Length == 0 || text.All(char.IsDigit)) continue;
                var c = Regex.Escape(text);
                foreach (var follow in new[] { ",", ".", " ", ";" })
                    datis = Regex.Replace(datis, @"(?<!@)\b" + c + @"\b" + Regex.Escape(follow),
                        variable.Replace("$", "$$") + follow.Replace("$", "$$"));
            }
        }

        const string marker = "NOTAMS... ";
        var idx = datis.IndexOf(marker, StringComparison.Ordinal);
        return idx >= 0
            ? new FormattedAtis(datis[..idx], datis[(idx + marker.Length)..])
            : new FormattedAtis(datis, "");
    }

    /// <summary>
    /// Builds the contraction list like contraction_replacements(): first variable wins for
    /// duplicate text, longest text first.
    /// </summary>
    public static List<KeyValuePair<string, string>> BuildContractions(
        IEnumerable<KeyValuePair<string, string>> variableToText)
    {
        var seen = new Dictionary<string, string>();
        foreach (var (variable, text) in variableToText)
            if (!seen.ContainsKey(text)) seen[text] = "@" + variable;
        return seen.OrderByDescending(kv => kv.Key.Length).ToList();
    }

    private static bool TryParseUpdatedAt(string? s, out DateTimeOffset result)
    {
        result = default;
        if (string.IsNullOrWhiteSpace(s)) return false;
        // Original truncates to 26 chars and treats the value as UTC.
        var trimmed = s.Length > 26 ? s[..26] : s;
        return DateTimeOffset.TryParse(trimmed, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out result);
    }

    /// <summary>Python-style \1 backreferences -> .NET $1; literal $ is escaped.</summary>
    private static string ToDotNetReplacement(string r) =>
        Regex.Replace(r.Replace("$", "$$"), @"\\(\d)", "$${$1}");
}
