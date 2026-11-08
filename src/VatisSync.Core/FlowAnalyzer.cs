using System.Text.RegularExpressions;

namespace VatisSync.Core;

/// <summary>
/// The airport "flow" an ATIS describes: which runways are in use and which approach types are expected.
/// Two texts describe the same flow when their signatures are equal.
/// </summary>
public sealed record FlowSignature(IReadOnlyList<string> Runways, IReadOnlyList<string> Approaches)
{
    public static FlowSignature Empty { get; } = new([], []);

    public bool IsEmpty => Runways.Count == 0 && Approaches.Count == 0;

    public bool SameAs(FlowSignature other) =>
        Runways.SequenceEqual(other.Runways) && Approaches.SequenceEqual(other.Approaches);

    /// <summary>e.g. "runways 7L, 7R, 8; ILS, VISUAL".</summary>
    public string Describe() => IsEmpty
        ? "no runways or approaches"
        : $"runways {(Runways.Count == 0 ? "none" : string.Join(", ", Runways))}" +
          (Approaches.Count == 0 ? "" : $"; {string.Join(", ", Approaches)}");
}

/// <summary>
/// Reads the runway configuration out of an Airport Conditions text, so the app can tell when a refresh
/// would switch the airport's flow. Only sentences that describe the flow count (runways in use, landing,
/// departing, expected approaches); closures, hold-short instructions and similar sentences are ignored so
/// that mentioning a closed runway is not mistaken for a flow change.
/// </summary>
public static class FlowAnalyzer
{
    private static readonly Regex Sentence = new(@"(?<=\.)\s+", RegexOptions.Compiled);

    // A sentence describes the flow if it says something like these...
    private static readonly Regex FlowWords = new(
        @"\b(?:IN USE|DEPG|DEPTG|DEPS?|DEPARTING|DEPARTURES?|LNDG|LANDING|ARRIVALS?|EXPECT|APCHS?|APPROACH(?:ES)?|SIMUL|SIMULTANEOUS|ILS|RNAV|RNP|LDA|VIS|VISUAL)\b",
        RegexOptions.Compiled);

    // ...and not like these, which talk about a runway without saying it is the flow.
    private static readonly Regex NotFlowWords = new(
        @"\b(?:CLSD|CLOSED|OTS|OUT OF SERVICE|HOLD SHORT|BTN|BETWEEN|CAUTION|CRANES?|BRAKING|CONDITIONS?|RSC|RCC|FICON|RWSL)\b|\bHS\b",
        RegexOptions.Compiled);

    // Runway numbers after RWY / RY / RUNWAY or after a flow word, as real ATIS writes them:
    //   RWY 28R   @RWY 8   RWYS 24 AND 25   DEPG RWYS, 26L, 27R   RWY8, RWY17L   VIS 26R, VIS 27L   EXPECT 1R, 36
    // A bare number is not taken as a runway when it is a quantity (EXPECT 30 MIN DELAYS).
    private static readonly Regex RunwayList = new(
        @"(?<![A-Z])(?:@?RWYS?|@?RY|RUNWAYS?|ILS|RNAV|RNP|LDA|LOC|VOR|GPS|VIS|VISUAL|DEPS?|DEPG|DEPTG|DEPARTING|EXPECT|LNDG|LANDING)\s*,?\s*" +
        @"(?<list>(?!\d{1,2}\s*(?:MIN|HR|HOUR|KT|FT|FEET|NM|MILE|PERCENT|%))\d{1,2}[LRC]?" +
        @"(?:\s*(?:,\s*(?:AND|OR)?|/|AND|OR|&)\s*(?:RWYS?\s*)?\d{1,2}[LRC]?)*)",
        RegexOptions.Compiled);

    private static readonly Regex RunwayToken = new(@"\d{1,2}[LRC]?", RegexOptions.Compiled);

    private static readonly Regex ApproachType = new(
        @"\b(?:ILS|RNAV|RNP|LDA|LOC|LOCALIZER|VOR|GPS|NDB|TACAN|VISUAL|VIS)\b", RegexOptions.Compiled);

    public static FlowSignature Analyze(string? airportConditions)
    {
        var runways = new SortedSet<string>(StringComparer.Ordinal);
        var approaches = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var raw in Sentence.Split(airportConditions ?? ""))
        {
            var sentence = raw.ToUpperInvariant();
            if (!FlowWords.IsMatch(sentence) || NotFlowWords.IsMatch(sentence)) continue;

            foreach (Match m in RunwayList.Matches(sentence))
                foreach (Match r in RunwayToken.Matches(m.Groups["list"].Value))
                    runways.Add(r.Value);

            foreach (Match m in ApproachType.Matches(sentence))
                approaches.Add(m.Value switch { "LOCALIZER" => "LOC", "VIS" => "VISUAL", _ => m.Value });
        }

        return new FlowSignature([.. runways], [.. approaches]);
    }

    /// <summary>True when the two texts describe a different runway configuration or approach type.</summary>
    public static bool FlowChanged(string? current, string? updated) =>
        !Analyze(current).SameAs(Analyze(updated));
}
