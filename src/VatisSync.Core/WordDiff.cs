namespace VatisSync.Core;

/// <summary>A word of text and whether it differs from the other version.</summary>
public readonly record struct DiffWord(string Text, bool Changed);

/// <summary>Word-level comparison of two texts, used to highlight what an update would change.</summary>
public static class WordDiff
{
    /// <summary>
    /// Both texts split into words, each marked when it is not part of the longest common sequence of words.
    /// </summary>
    public static (IReadOnlyList<DiffWord> Old, IReadOnlyList<DiffWord> New) Compare(string? oldText, string? newText)
    {
        var a = Split(oldText);
        var b = Split(newText);

        // Longest common subsequence table (texts are a few hundred words at most).
        var lcs = new int[a.Length + 1, b.Length + 1];
        for (var i = a.Length - 1; i >= 0; i--)
            for (var j = b.Length - 1; j >= 0; j--)
                lcs[i, j] = a[i] == b[j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);

        var oldWords = new List<DiffWord>(a.Length);
        var newWords = new List<DiffWord>(b.Length);
        int x = 0, y = 0;
        while (x < a.Length && y < b.Length)
        {
            if (a[x] == b[y]) { oldWords.Add(new(a[x++], false)); newWords.Add(new(b[y++], false)); }
            else if (lcs[x + 1, y] >= lcs[x, y + 1]) oldWords.Add(new(a[x++], true));
            else newWords.Add(new(b[y++], true));
        }
        while (x < a.Length) oldWords.Add(new(a[x++], true));
        while (y < b.Length) newWords.Add(new(b[y++], true));

        return (oldWords, newWords);
    }

    /// <summary>
    /// One combined comparison: words in both texts are <see cref="DiffKind.Same"/>, words only in the old text are
    /// <see cref="DiffKind.Removed"/>, words only in the new text are <see cref="DiffKind.Added"/>. Neighbouring words of the
    /// same kind are joined into one segment, separated by single spaces.
    /// </summary>
    public static IReadOnlyList<DiffSegment> Unified(string? oldText, string? newText)
    {
        var a = Split(oldText);
        var b = Split(newText);

        var lcs = new int[a.Length + 1, b.Length + 1];
        for (var i = a.Length - 1; i >= 0; i--)
            for (var j = b.Length - 1; j >= 0; j--)
                lcs[i, j] = a[i] == b[j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);

        var segments = new List<DiffSegment>();
        void Add(string word, DiffKind kind)
        {
            if (segments.Count > 0 && segments[^1].Kind == kind)
                segments[^1] = segments[^1] with { Text = segments[^1].Text + " " + word };
            else
                segments.Add(new DiffSegment(word, kind));
        }

        int x = 0, y = 0;
        while (x < a.Length && y < b.Length)
        {
            if (a[x] == b[y]) { Add(a[x], DiffKind.Same); x++; y++; }
            else if (lcs[x + 1, y] >= lcs[x, y + 1]) Add(a[x++], DiffKind.Removed);
            else Add(b[y++], DiffKind.Added);
        }
        while (x < a.Length) Add(a[x++], DiffKind.Removed);
        while (y < b.Length) Add(b[y++], DiffKind.Added);
        return segments;
    }

    private static string[] Split(string? text) =>
        (text ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
}
