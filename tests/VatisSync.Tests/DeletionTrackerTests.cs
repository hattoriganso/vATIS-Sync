using VatisSync.Core;

namespace VatisSync.Tests;

public class DeletionTrackerTests
{
    [Fact]
    public void Finds_a_deleted_run_of_words()
    {
        Assert.Equal(["B C"], DeletionTracker.FindDeletedPortions("A B C D", "A D"));
    }

    [Fact]
    public void Finds_multiple_separate_deletions()
    {
        Assert.Equal(["B", "D"], DeletionTracker.FindDeletedPortions("A B C D E", "A C E"));
    }

    [Fact]
    public void Deleting_at_the_start_or_end_is_found()
    {
        Assert.Equal(["A"], DeletionTracker.FindDeletedPortions("A B C", "B C"));
        Assert.Equal(["C"], DeletionTracker.FindDeletedPortions("A B C", "A B"));
    }

    [Fact]
    public void Deleting_everything_is_one_deletion()
    {
        Assert.Equal(["A B C"], DeletionTracker.FindDeletedPortions("A B C", ""));
    }

    [Theory]
    [InlineData("A B C", "A B C")]       // unchanged
    [InlineData("A B C", "A X C")]       // replaced, not deleted
    [InlineData("A B C", "A B NEW C")]   // pure insertion
    [InlineData("", "A B")]              // nothing to delete from
    [InlineData("", "")]
    public void No_deletion_reported(string before, string after)
    {
        Assert.Empty(DeletionTracker.FindDeletedPortions(before, after));
    }

    [Fact]
    public void Only_the_deleted_part_of_an_edit_is_reported()
    {
        // B->X is an edit, D is a true deletion.
        Assert.Equal(["D"], DeletionTracker.FindDeletedPortions("A B C D E", "A X C E"));
    }

    [Fact]
    public void Whitespace_differences_do_not_count_as_deletions()
    {
        Assert.Empty(DeletionTracker.FindDeletedPortions("A B  C", "A B C"));
    }

    [Fact]
    public void Compare_collects_deletions_from_both_fields_per_station()
    {
        var before = new Dictionary<string, DeletionTracker.AtisText>
        {
            ["KSFO"] = new("RWY 28 IN USE. TWY B CLSD.", "TWY C CLSD. CRANE NEAR RWY 1."),
            ["KOAK"] = new("RWY 30 IN USE.", ""),
            ["KSJC"] = new("RWY 12 IN USE.", ""),
        };
        var after = new Dictionary<string, DeletionTracker.AtisText>
        {
            ["KSFO"] = new("RWY 28 IN USE.", "TWY C CLSD."),
            ["KOAK"] = new("RWY 30 IN USE.", ""),
            // KSJC disappeared (disconnected): ignored
        };

        var result = DeletionTracker.Compare(before, after);

        Assert.Equal(["KSFO"], result.Keys);
        Assert.Equal(["TWY B CLSD.", "CRANE NEAR RWY 1."], result["KSFO"]);
    }

    [Fact]
    public void Expand_turns_variables_back_into_text()
    {
        var vars = new[] { new KeyValuePair<string, string>("RWY", "RUNWAY") };
        Assert.Equal(["RUNWAY 28"], DeletionTracker.ExpandContractions(["@RWY 28"], vars));
    }

    [Fact]
    public void Expand_prefers_the_longer_variable_name()
    {
        var vars = new[]
        {
            new KeyValuePair<string, string>("RWY", "RUNWAY"),
            new KeyValuePair<string, string>("RWYS", "RUNWAYS"),
        };
        Assert.Equal(["RUNWAYS 28"], DeletionTracker.ExpandContractions(["@RWYS 28"], vars));
    }

    [Fact]
    public void Expand_drops_portions_that_end_up_empty()
    {
        Assert.Empty(DeletionTracker.ExpandContractions(["  "], []));
    }

    [Fact]
    public void Removal_rules_match_literally_not_as_regex()
    {
        var rule = DeletionTracker.AsRemovalRules(["TWY B (CLSD) 1+1."]).Single();
        Assert.Equal("", rule.Value);

        // Via the formatter: the deleted text must disappear, and nothing else may be affected.
        var entry = new DatisEntry("KSFO", "combined",
            "H1. H2. KEEP THIS. TWY B (CLSD) 1+1. AND THIS. ...ADVS YOU HAVE INFO A.",
            DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.ffffff"));
        var r = DatisFormatter.Format("KSFO", [entry], DateTimeOffset.UtcNow, [rule]);
        Assert.Equal("KEEP THIS. AND THIS.", r.AirportConditions.Trim());
    }

    [Fact]
    public void Removal_rules_tolerate_irregular_spacing_in_the_source()
    {
        // KLAX's real feed has "PUSH OR  TAXI" (two spaces); the user sees it normalized.
        var rule = DeletionTracker.AsRemovalRules(["PUSH OR TAXI"]).Single();
        var entry = new DatisEntry("KLAX", "combined",
            "H1. H2. KEEP. CTC GC FOR PUSH OR  TAXI ON A. ...ADVS YOU HAVE INFO Y.",
            DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.ffffff"));
        var r = DatisFormatter.Format("KLAX", [entry], DateTimeOffset.UtcNow, [rule]);
        Assert.DoesNotContain("PUSH", r.AirportConditions);
        Assert.Contains("KEEP.", r.AirportConditions);
    }

    [Fact]
    public void Handles_long_inputs_quickly()
    {
        var words = Enumerable.Range(0, 400).Select(i => "W" + (i % 37)).ToArray();
        var before = string.Join(" ", words);
        var after = string.Join(" ", words.Where((_, i) => i % 10 != 0));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var deleted = DeletionTracker.FindDeletedPortions(before, after);
        Assert.NotEmpty(deleted);
        Assert.True(sw.ElapsedMilliseconds < 2000, $"took {sw.ElapsedMilliseconds} ms");
    }
}
