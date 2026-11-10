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
/// departing, expected approaches); closures, hold-short instructions and the like are ignored, part of a
/// sentence by part, so that mentioning a closed runway is not mistaken for a flow change and does not hide one.
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

    // A comma-separated part that carries on a runway list: "RWY 27 CLSD" in "RWY 9, RWY 27 CLSD"...
    private static readonly Regex StartsWithRunway = new(
        @"^\s*(?:(?:@?RWYS?|@?RY|RUNWAYS?)\s*)?\d{1,2}[LRC]?\b", RegexOptions.Compiled);

    // ...and one that is only the number, so what it says goes for the whole list: "27 OTS" in "ILS RWY 8R, 9, 27 OTS".
    private static readonly Regex StartsWithBareRunway = new(@"^\s*\d{1,2}[LRC]?\b", RegexOptions.Compiled);

    public static FlowSignature Analyze(string? airportConditions)
    {
        var runways = new SortedSet<string>(StringComparer.Ordinal);
        var approaches = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var raw in Sentence.Split(airportConditions ?? ""))
        {
            var sentence = raw.ToUpperInvariant();
            if (!FlowWords.IsMatch(sentence)) continue;

            // A closure rules out its own part of the sentence, not the whole of it, so the flow in
            // "LNDG RWY 9, RWY 27 CLSD" is still read (runway 9) and a change to it is still seen.
            var parts = Parts(sentence);
            var notFlow = NotFlowParts(sentence, parts);
            var mixed = notFlow.Count > 0;

            foreach (Match m in RunwayList.Matches(sentence))
            {
                // Beside a closure, a runway counts only where the sentence says it is the flow: the
                // "RWY 14R CAT TWO" in "ILS RWY 18 OTS, RWY 14R CAT TWO NOT AUTHORIZED" is not.
                if (mixed && (Within(notFlow, m.Index) || !StatesFlow(sentence, parts, m))) continue;

                var list = m.Groups["list"];
                foreach (Match r in RunwayToken.Matches(list.Value))
                    if (!Within(notFlow, list.Index + r.Index)) runways.Add(r.Value);
            }

            foreach (Match m in ApproachType.Matches(sentence))
                if (!Within(notFlow, m.Index))
                    approaches.Add(m.Value switch { "LOCALIZER" => "LOC", "VIS" => "VISUAL", _ => m.Value });
        }

        return new FlowSignature([.. runways], [.. approaches]);
    }

    /// <summary>The comma- or semicolon-separated parts of a sentence, as offsets into it.</summary>
    private static List<(int Start, int End)> Parts(string sentence)
    {
        var parts = new List<(int Start, int End)>();
        var start = 0;
        for (var i = 0; i <= sentence.Length; i++)
        {
            if (i < sentence.Length && sentence[i] is not (',' or ';')) continue;
            parts.Add((start, i));
            start = i + 1;
        }
        return parts;
    }

    /// <summary>
    /// The parts of a sentence that talk about a runway without saying it is the flow. A part that carries on a
    /// runway list takes the earlier parts of that list with it: all of them when it is only a number
    /// ("ILS RWY 8R, 9, 27 OTS": none of the three), otherwise up to one that states the flow itself
    /// ("RWY 9, RWY 27 CLSD": both, but "LNDG RWY 9, RWY 27 CLSD": only 27).
    /// </summary>
    private static List<(int Start, int End)> NotFlowParts(string sentence, List<(int Start, int End)> parts)
    {
        string Text(int p) => sentence[parts[p].Start..parts[p].End];

        var result = new List<(int Start, int End)>();
        for (var p = 0; p < parts.Count; p++)
        {
            var text = Text(p);
            if (!NotFlowWords.IsMatch(text)) continue;
            result.Add(parts[p]);

            if (StartsWithBareRunway.IsMatch(text))
            {
                for (var q = p - 1; q >= 0; q--)
                {
                    result.Add(parts[q]);
                    if (!StartsWithBareRunway.IsMatch(Text(q))) break; // the head of the list
                }
            }
            else if (StartsWithRunway.IsMatch(text))
            {
                for (var q = p - 1; q >= 0 && !FlowWords.IsMatch(Text(q)); q--)
                    result.Add(parts[q]);
            }
        }
        return result;
    }

    private static bool Within(List<(int Start, int End)> parts, int index) =>
        parts.Exists(p => index >= p.Start && index < p.End);

    /// <summary>True when one of the parts the runway list runs through has a word that states the flow.</summary>
    private static bool StatesFlow(string sentence, List<(int Start, int End)> parts, Match list) =>
        parts.Exists(p => p.Start < list.Index + list.Length && p.End > list.Index &&
                          FlowWords.IsMatch(sentence[p.Start..p.End]));

    /// <summary>True when the two texts describe a different runway configuration or approach type.</summary>
    public static bool FlowChanged(string? current, string? updated) =>
        !Analyze(current).SameAs(Analyze(updated));
}
