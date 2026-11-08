using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace VatisSync.Core;

/// <summary>Which position a frequency in the D-ATIS text belongs to.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FrequencyRole
{
    ClearanceDelivery,
    Ground,
    Tower,
    Departure,
    Approach,
    Ramp,
    Metering,
    Helicopter,

    /// <summary>A frequency whose position the text does not name (for example "RWY 9 FREQ 123.9").</summary>
    Other,
}

/// <summary>What to do with the frequencies of one role.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FrequencyMode
{
    /// <summary>Keep the real-world text.</summary>
    Off,

    /// <summary>Remove the instruction.</summary>
    Remove,

    /// <summary>Replace the frequency with one the user chose.</summary>
    Override,
}

/// <summary>A role's mode plus, for <see cref="FrequencyMode.Override"/>, the frequency to write (for example "121.900").</summary>
public sealed record FrequencySetting(FrequencyMode Mode = FrequencyMode.Off, string? Frequency = null);

/// <summary>One frequency instruction found in the text. Offsets index into the text given to <see cref="FrequencyDetector.Detect"/>.</summary>
/// <param name="Roles">Positions named by the instruction. Empty when it names none (treated as <see cref="FrequencyRole.Other"/>).</param>
/// <param name="Ignored">The instruction is about something this feature never touches (the ATIS frequency, Flight Service).</param>
/// <param name="Start">Start of the instruction (inclusive).</param>
/// <param name="End">End of the instruction, including a trailing "FOR TAXI"-style qualifier (exclusive).</param>
/// <param name="FreqStart">Start of the frequency itself.</param>
/// <param name="FreqEnd">End of the frequency itself.</param>
public sealed record FrequencyMention(
    IReadOnlySet<FrequencyRole> Roles, bool Ignored, int Start, int End, int FreqStart, int FreqEnd, string Frequency);

/// <summary>
/// Finds frequency instructions in D-ATIS text and applies a per-role policy to them. A frequency is the anchor:
/// text that mentions a position but has no frequency (for example "CONTACT CD 10 MIN PRIOR TO PUSH") is never touched.
/// Each instruction's extent stops at clause boundaries (commas, semicolons, sentence ends), so neighbouring items in
/// the same sentence are left alone. Several role+frequency pairs in one clause are handled one by one.
/// </summary>
public static class FrequencyDetector
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(1);
    private const RegexOptions Opts = RegexOptions.Compiled | RegexOptions.CultureInvariant;

    // Civil VHF comm band. The lower limit keeps nav/ILS frequencies (108-117.95) out.
    private const double MinMhz = 118.0, MaxMhz = 137.0;

    private const string DigitWord = "(?:ZERO|ONE|TWO|THREE|TREE|FOUR|FIVE|SIX|SEVEN|EIGHT|NINE|NINER)";

    private static readonly Regex Numeric = new(@"(?<![\d.])1[0-3]\d\.\d{1,3}(?!\d)", Opts, Timeout);

    private static readonly Regex Spelled = new(
        $@"\b{DigitWord}(?:\s+{DigitWord}){{2}}\s+(?:POINT|DECIMAL)(?:\s+{DigitWord}){{1,3}}\b", Opts, Timeout);

    /// <summary>Sentence ends: a period (or "...") followed by space or the end, but not a decimal point.</summary>
    private static readonly Regex SentenceEnd = new(@"\.+(?=\s|$)", Opts, Timeout);

    private static readonly Regex ClauseBreak = new(@"[,;]", Opts, Timeout);

    /// <summary>A comma-separated bit that is only a runway, such as the "RWY 8L," in "RWY 8L, RWY 8R FREQ 118.3".</summary>
    private static readonly Regex RunwayOnly = new(@"^\s*RWYS?\s+\d{1,2}[LRC]?\s*$", Opts, Timeout);

    // Preceded immediately by this, the frequency is the ATIS frequency itself.
    private static readonly Regex AtisBefore = new(
        @"\bATIS\s+(?:IN\s+USE\s+)?(?:ON\s+)?(?:FREQ(?:UENCY)?\s+)?(?:IS\s+)?$", Opts, Timeout);

    // Compound names go first so "GND METERING" is metering, not ground + metering.
    private static readonly (FrequencyRole Role, Regex Pattern)[] RolePatterns =
    [
        (FrequencyRole.Metering, new(@"\b(?:(?:GND|GROUND)\s+)?METERING\b", Opts, Timeout)),
        (FrequencyRole.Ramp, new(@"\b(?:RAMP|APRON)\b", Opts, Timeout)),
        (FrequencyRole.Helicopter, new(@"\bHELI(?:COPTOR|COPTER|PAD)?\b", Opts, Timeout)),
        (FrequencyRole.ClearanceDelivery, new(@"\b(?:CLEARANCE|CLNC|CLR|DLVRY|DLVY|DELIVERY|CD)\b", Opts, Timeout)),
        (FrequencyRole.Tower, new(@"\b(?:TWR|TOWER)\b", Opts, Timeout)),
        (FrequencyRole.Ground, new(@"\b(?:GND|GC|GROUND)\b", Opts, Timeout)),
        // "DEPG" (departing) and "DEPARTURES" (the aircraft) are not the position, so only the singular forms count.
        (FrequencyRole.Departure, new(@"\b(?:DEPARTURE|DEP)\b|\bDEPT\b(?=\s+FREQ)", Opts, Timeout)),
        (FrequencyRole.Approach, new(@"\b(?:APCH|APPROACH|APP|CLASS\s+[BCD])\b", Opts, Timeout)),
    ];

    private static readonly Regex IgnoredRole = new(@"\b(?:A?FSS|FLT\s+SVC|FLIGHT\s+SERVICE)\b", Opts, Timeout);

    /// <summary>Every frequency instruction in <paramref name="text"/>, in order.</summary>
    public static IReadOnlyList<FrequencyMention> Detect(string text)
    {
        var result = new List<FrequencyMention>();
        var sentenceStart = 0;
        foreach (var end in SentenceBounds(text))
        {
            DetectInSentence(text, sentenceStart, end, result);
            sentenceStart = end;
        }
        return result;
    }

    /// <summary>
    /// Applies the policy and returns the new text. Roles that are Off, or that have no usable setting, are left alone.
    /// An instruction naming several roles (for example "CD AND GC COMBINED ON 123.9") changes only when every role has
    /// the same decision, so a position that is still real is never stripped because another one was switched on.
    /// </summary>
    public static string Apply(string text, IReadOnlyDictionary<FrequencyRole, FrequencySetting>? policy)
    {
        if (policy is null || policy.Count == 0 || string.IsNullOrEmpty(text)) return text;

        var removals = new List<(int Start, int End)>();
        var edits = new List<(int Start, int End, string Replacement)>();
        foreach (var m in Detect(text))
        {
            if (m.Ignored) continue;
            var roles = m.Roles.Count == 0 ? new HashSet<FrequencyRole> { FrequencyRole.Other } : m.Roles;
            var settings = roles.Select(r => policy.TryGetValue(r, out var s) ? Usable(s) : new FrequencySetting()).ToList();

            if (settings.All(s => s.Mode == FrequencyMode.Remove))
                removals.Add((m.Start, m.End));
            else if (settings.All(s => s.Mode == FrequencyMode.Override) &&
                     settings.Select(s => s.Frequency).Distinct().Count() == 1)
                edits.Add((m.FreqStart, m.FreqEnd, settings[0].Frequency!));
        }

        // Instructions that sit side by side ("TWR FREQ 10L IS 119.3 TWR FREQ 10R IS 120.2", "FREQ 127.675 OTS, USE 124.95") go as one cut, so the
        // punctuation around them is tidied once, for the whole piece.
        var merged = new List<(int Start, int End)>();
        foreach (var r in removals)
        {
            if (merged.Count > 0 && text[merged[^1].End..r.Start].All(c => char.IsWhiteSpace(c) || c is ',' or ';'))
                merged[^1] = (merged[^1].Start, r.End);
            else merged.Add(r);
        }
        edits.AddRange(merged.Select(r => TidyCut(text, r.Start, r.End)));

        if (edits.Count == 0) return text;
        var sb = new StringBuilder(text.Length);
        var pos = 0;
        foreach (var (start, end, replacement) in edits.OrderBy(e => e.Start))
        {
            if (start < pos) continue; // overlapping edit; cannot happen with the extents above, but never corrupt text
            sb.Append(text, pos, start - pos).Append(replacement);
            pos = end;
        }
        return sb.Append(text, pos, text.Length - pos).ToString();
    }

    /// <summary>True for "118.0" to "136.999" with up to three decimals, the shape the override setting accepts.</summary>
    public static bool IsValidFrequency(string? value) =>
        value is not null && Regex.IsMatch(value, @"^1[0-3]\d\.\d{1,3}$", RegexOptions.CultureInvariant) &&
        InBand(double.Parse(value, CultureInfo.InvariantCulture));

    /// <summary>A setting with an invalid override frequency counts as Off: better to keep real text than write nonsense.</summary>
    public static FrequencySetting Usable(FrequencySetting setting) =>
        setting.Mode == FrequencyMode.Override && !IsValidFrequency(setting.Frequency?.Trim())
            ? new FrequencySetting()
            : setting.Mode == FrequencyMode.Override ? setting with { Frequency = setting.Frequency!.Trim() } : setting;

    // ---- detection ----

    private static IEnumerable<int> SentenceBounds(string text)
    {
        var last = 0;
        foreach (Match m in SentenceEnd.Matches(text)) { last = m.Index + m.Length; yield return last; }
        if (last < text.Length) yield return text.Length;
    }

    private static void DetectInSentence(string text, int sStart, int sEnd, List<FrequencyMention> into)
    {
        var sentence = text[sStart..sEnd];
        var freqs = FindFrequencies(sentence);
        if (freqs.Count == 0) return;

        // Clause boundaries: commas and semicolons, except a comma that only follows a runway ("RWY 8L, RWY 8R FREQ ...").
        var breaks = new List<int> { 0 };
        var lastComma = 0;
        foreach (Match b in ClauseBreak.Matches(sentence))
        {
            // Only what follows the last frequency can be a runway that belongs to the next instruction.
            var from = lastComma;
            var prior = freqs.LastOrDefault(f => f.End <= b.Index && f.End > from);
            if (prior is not null) from = prior.End;
            var skip = RunwayOnly.IsMatch(sentence[from..b.Index]);
            lastComma = b.Index + 1;
            if (skip) continue;
            breaks.Add(b.Index + 1);
        }
        breaks.Add(sentence.Length);

        for (var c = 0; c < breaks.Count - 1; c++)
        {
            var clauseStart = breaks[c];
            var clauseEnd = breaks[c + 1];
            var inClause = freqs.Where(f => f.Start >= clauseStart && f.End <= clauseEnd).ToList();
            if (inClause.Count == 0) continue;

            IReadOnlySet<FrequencyRole>? inherited = null;
            for (var i = 0; i < inClause.Count; i++)
            {
                var f = inClause[i];
                var start = i == 0 ? clauseStart : inClause[i - 1].End;
                var last = i == inClause.Count - 1;
                var extentEnd = last ? TrimEnd(sentence, f.End, clauseEnd) : f.End;

                var head = sentence[start..f.Start];
                var ignored = AtisBefore.IsMatch(head) || IgnoredRole.IsMatch(head);
                var roles = RolesIn(head);
                if (roles.Count == 0 && inherited is not null) roles = [.. inherited];
                if (roles.Count == 0 && last) roles = RolesIn(sentence[f.End..extentEnd]); // "CTC 121.7 FOR CLNC DLVRY"
                inherited = roles.Count > 0 ? roles : inherited;

                // Skip leading separators and spaces so the extent starts at a word.
                while (start < f.Start && (char.IsWhiteSpace(sentence[start]) || sentence[start] is ',' or ';')) start++;

                into.Add(new FrequencyMention(roles, ignored, sStart + start, sStart + extentEnd,
                    sStart + f.Start, sStart + f.End, f.Value));
            }
        }
    }

    private static int TrimEnd(string sentence, int from, int clauseEnd)
    {
        var end = clauseEnd;
        // Do not swallow the clause's closing separator or the sentence's period.
        while (end > from && (char.IsWhiteSpace(sentence[end - 1]) || sentence[end - 1] is ',' or ';' or '.')) end--;
        return end;
    }

    private sealed record Freq(int Start, int End, string Value);

    private static List<Freq> FindFrequencies(string sentence)
    {
        var list = new List<Freq>();
        foreach (Match m in Numeric.Matches(sentence))
            if (InBand(double.Parse(m.Value, CultureInfo.InvariantCulture)))
                list.Add(new Freq(m.Index, m.Index + m.Length, m.Value));

        foreach (Match m in Spelled.Matches(sentence))
        {
            var value = SpelledToNumber(m.Value);
            if (value is not null && InBand(double.Parse(value, CultureInfo.InvariantCulture)))
                list.Add(new Freq(m.Index, m.Index + m.Length, value));
        }
        return list.OrderBy(f => f.Start).ToList();
    }

    private static HashSet<FrequencyRole> RolesIn(string text)
    {
        var roles = new HashSet<FrequencyRole>();
        foreach (var (role, pattern) in RolePatterns)
        {
            if (!pattern.IsMatch(text)) continue;
            roles.Add(role);
            // Consume what matched so "GND METERING" does not also count as Ground.
            if (role == FrequencyRole.Metering) text = pattern.Replace(text, " ");
        }
        return roles;
    }

    private static bool InBand(double mhz) => mhz >= MinMhz && mhz < MaxMhz;

    private static string? SpelledToNumber(string spelled)
    {
        var sb = new StringBuilder();
        foreach (var word in spelled.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (word is "POINT" or "DECIMAL") { sb.Append('.'); continue; }
            var d = word switch
            {
                "ZERO" => '0', "ONE" => '1', "TWO" => '2', "THREE" or "TREE" => '3', "FOUR" => '4',
                "FIVE" => '5', "SIX" => '6', "SEVEN" => '7', "EIGHT" => '8', "NINE" or "NINER" => '9',
                _ => '\0',
            };
            if (d == '\0') return null;
            sb.Append(d);
        }
        return sb.ToString();
    }

    // ---- editing ----

    /// <summary>
    /// What to cut to remove the instruction without leaving punctuation behind: the separator after it if more
    /// follows in the sentence, or the separator before it if it ends the sentence.
    /// </summary>
    private static (int Start, int End, string Replacement) TidyCut(string text, int start, int end)
    {
        var after = end;
        while (after < text.Length && text[after] == ' ') after++;
        if (after < text.Length && text[after] is ',' or ';')
        {
            end = after + 1; // more follows: take the instruction's own trailing separator
        }
        else
        {
            var before = start;
            while (before > 0 && text[before - 1] == ' ') before--;
            if (before > 0 && text[before - 1] is ',' or ';')
                start = before - 1; // it was the last clause
            else if ((before == 0 || text[before - 1] == '.') && after < text.Length && text[after] == '.' &&
                     (after + 1 >= text.Length || text[after + 1] != '.'))
                end = after + 1; // it was the whole sentence: take its period too
        }
        return (start, end, "");
    }
}
