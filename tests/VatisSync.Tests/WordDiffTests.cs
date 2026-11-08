using VatisSync.Core;

namespace VatisSync.Tests;

public class WordDiffTests
{
    private static string Changed(IReadOnlyList<DiffWord> words) =>
        string.Join(" ", words.Where(w => w.Changed).Select(w => w.Text));

    [Fact]
    public void Marks_only_the_words_that_differ()
    {
        var (o, n) = WordDiff.Compare("ILS RWY 28R IN USE. TWY B CLSD.", "ILS RWY 10L IN USE. TWY B CLSD.");
        Assert.Equal("28R", Changed(o));
        Assert.Equal("10L", Changed(n));
    }

    [Fact]
    public void Handles_additions_and_removals()
    {
        var (o, n) = WordDiff.Compare("A B C", "A C D E");
        Assert.Equal("B", Changed(o));
        Assert.Equal("D E", Changed(n));
    }

    [Fact]
    public void Identical_and_empty_texts_have_no_changes()
    {
        var (o, n) = WordDiff.Compare("SAME TEXT", "SAME   TEXT");
        Assert.Empty(Changed(o));
        Assert.Empty(Changed(n));
        var (o2, n2) = WordDiff.Compare(null, "NEW");
        Assert.Empty(o2);
        Assert.Equal("NEW", Changed(n2));
    }

    [Fact]
    public void Words_keep_their_original_order()
    {
        var (o, _) = WordDiff.Compare("ONE TWO THREE", "ONE THREE");
        Assert.Equal(["ONE", "TWO", "THREE"], o.Select(w => w.Text));
    }
}
