using System.Text.RegularExpressions;

namespace VatisSync.Core;

/// <summary>
/// Notices text the user deleted by hand in vATIS, so later refreshes don't put it back.
/// The original script diffed character by character with difflib; this diffs by word, which avoids
/// fragments like "ILS " being treated as a deletion rule.
/// </summary>
public static class DeletionTracker
{
    /// <summary>(Airport conditions, NOTAMs) as shown in vATIS.</summary>
    public readonly record struct AtisText(string AirportConditions, string Notams);

    /// <summary>Portions of <paramref name="original"/> that are missing (purely deleted) from <paramref name="modified"/>.</summary>
    public static List<string> FindDeletedPortions(string original, string modified)
    {
        var a = Tokenize(original);
        var b = Tokenize(modified);
        var blocks = MatchingBlocks(a, b);

        var deleted = new List<string>();
        int ai = 0, bj = 0;
        foreach (var (i, j, n) in blocks.Append((a.Length, b.Length, 0)))
        {
            // A pure deletion: tokens only in the original, nothing new in the modified text at this spot.
            if (ai < i && bj == j)
            {
                deleted.Add(string.Join(" ", a[ai..i]));
            }
            ai = i + n;
            bj = j + n;
        }
        return deleted;
    }

    /// <summary>Per station, everything deleted from either field. Stations with no deletions are omitted.</summary>
    public static Dictionary<string, List<string>> Compare(
        IReadOnlyDictionary<string, AtisText> before, IReadOnlyDictionary<string, AtisText> after)
    {
        var result = new Dictionary<string, List<string>>();
        foreach (var (station, prev) in before)
        {
            if (!after.TryGetValue(station, out var now)) continue;
            var deleted = FindDeletedPortions(prev.AirportConditions, now.AirportConditions)
                .Concat(FindDeletedPortions(prev.Notams, now.Notams))
                .ToList();
            if (deleted.Count > 0) result[station] = deleted;
        }
        return result;
    }

    /// <summary>
    /// Text in vATIS holds contraction variables ("@RWY"), but rules are applied before contractions are swapped
    /// in, so turn variables back into their text. Longest variable first so "@RWYS" isn't clipped by "@RWY".
    /// </summary>
    public static List<string> ExpandContractions(
        IEnumerable<string> portions, IEnumerable<KeyValuePair<string, string>> variableToText)
    {
        var ordered = variableToText.OrderByDescending(kv => kv.Key.Length).ToList();
        return portions
            .Select(p => ordered.Aggregate(p, (s, kv) => s.Replace("@" + kv.Key, kv.Value)).Trim())
            .Where(p => p.Length > 0)
            .ToList();
    }

    /// <summary>
    /// Turns deleted text into replacement rules that remove it. Words match literally (never as regex),
    /// but any run of whitespace between them matches, since the source text can have irregular spacing.
    /// </summary>
    public static IEnumerable<KeyValuePair<string, string>> AsRemovalRules(IEnumerable<string> portions) =>
        portions
            .Select(p => Tokenize(p))
            .Where(words => words.Length > 0)
            .Select(words => new KeyValuePair<string, string>(string.Join(@"\s+", words.Select(Regex.Escape)), ""));

    /// <summary>Words only, so spacing differences never look like edits.</summary>
    private static string[] Tokenize(string s) =>
        Regex.Matches(s, @"\S+").Select(m => m.Value).ToArray();

    /// <summary>
    /// Matching blocks by repeatedly taking the longest common run of tokens (the same idea as difflib's
    /// SequenceMatcher, without its "autojunk" heuristic). Returned in order.
    /// </summary>
    private static List<(int I, int J, int N)> MatchingBlocks(string[] a, string[] b)
    {
        var blocks = new List<(int, int, int)>();
        var pending = new Stack<(int Alo, int Ahi, int Blo, int Bhi)>();
        pending.Push((0, a.Length, 0, b.Length));

        while (pending.Count > 0)
        {
            var (alo, ahi, blo, bhi) = pending.Pop();
            var (i, j, n) = LongestMatch(a, b, alo, ahi, blo, bhi);
            if (n == 0) continue;
            blocks.Add((i, j, n));
            if (alo < i && blo < j) pending.Push((alo, i, blo, j));
            if (i + n < ahi && j + n < bhi) pending.Push((i + n, ahi, j + n, bhi));
        }
        blocks.Sort();
        return blocks;
    }

    private static (int I, int J, int N) LongestMatch(string[] a, string[] b, int alo, int ahi, int blo, int bhi)
    {
        int bestI = alo, bestJ = blo, bestN = 0;
        var prev = new int[bhi - blo + 1];
        for (var i = alo; i < ahi; i++)
        {
            var cur = new int[bhi - blo + 1];
            for (var j = blo; j < bhi; j++)
            {
                if (a[i] != b[j]) continue;
                var k = cur[j - blo + 1] = prev[j - blo] + 1;
                if (k > bestN) { bestI = i - k + 1; bestJ = j - k + 1; bestN = k; }
            }
            prev = cur;
        }
        return (bestI, bestJ, bestN);
    }
}
