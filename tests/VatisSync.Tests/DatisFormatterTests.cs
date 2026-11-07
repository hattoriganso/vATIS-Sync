using VatisSync.Core;

namespace VatisSync.Tests;

public class DatisFormatterTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 7, 12, 0, 0, TimeSpan.Zero);

    private static DatisEntry Entry(string text, string type = "combined", string? updated = null) =>
        new("KSFO", type, text, updated ?? Now.AddMinutes(-10).ToString("yyyy-MM-ddTHH:mm:ss.ffffff"));

    private const string Sample =
        "SAN FRANCISCO INTERNATIONAL AIRPORT INFORMATION A. 1156Z. 28010KT 10SM CLR. ILS RWY 28R IN USE. " +
        "NOTICE TO AIR MISSIONS, NOTAMS. TWY B CLSD. ...ADVS YOU HAVE INFO A.";

    [Fact]
    public void Splits_conditions_and_notams_and_strips_header_and_tail()
    {
        var r = DatisFormatter.Format("KSFO", [Entry(Sample)], Now);
        Assert.True(r.IsAvailable);
        Assert.Equal("28010KT 10SM CLR. ILS RWY 28R IN USE.", r.AirportConditions.Trim());
        Assert.Equal("TWY B CLSD.", r.Notams.Trim());
    }

    [Fact]
    public void Stale_data_is_unavailable()
    {
        var old = Entry(Sample, updated: Now.AddHours(-2).ToString("yyyy-MM-ddTHH:mm:ss.ffffff"));
        Assert.False(DatisFormatter.Format("KSFO", [old], Now).IsAvailable);
    }

    [Fact]
    public void Unparseable_timestamp_is_ignored()
    {
        var e = new DatisEntry("KSFO", "combined", Sample, "garbage");
        Assert.True(DatisFormatter.Format("KSFO", [e], Now).IsAvailable);
    }

    [Fact]
    public void Missing_airport_or_wrong_type_is_unavailable()
    {
        Assert.False(DatisFormatter.Format("KOAK", [Entry(Sample)], Now).IsAvailable);
        Assert.False(DatisFormatter.Format("KSFO_A", [Entry(Sample)], Now).IsAvailable);
    }

    [Theory]
    [InlineData("KSFO", "combined")]
    [InlineData("KSFO_A", "arr")]
    [InlineData("KSFO_D", "dep")]
    public void Station_suffix_selects_type(string station, string expected) =>
        Assert.Equal(expected, DatisFormatter.AtisTypeFor(station));

    [Fact]
    public void Replacements_are_applied_and_percent_r_removed()
    {
        var reps = new[] { new KeyValuePair<string, string>("CLR", "CLEAR%r") };
        var r = DatisFormatter.Format("KSFO", [Entry(Sample)], Now, reps);
        Assert.Contains("CLEAR", r.AirportConditions);
        Assert.DoesNotContain("%r", r.AirportConditions);
    }

    [Fact]
    public void Contractions_replace_whole_words_and_prefer_longest()
    {
        var contractions = DatisFormatter.BuildContractions(
        [
            new("RWY", "RUNWAY"),
            new("ILSRWY", "ILS RWY"),
        ]);
        Assert.Equal("@ILSRWY", contractions[0].Value); // longer text first
        var r = DatisFormatter.Format("KSFO", [Entry(Sample)], Now, null,
            [new("RWY", "@RWY")]);
        Assert.Contains("@RWY 28R", r.AirportConditions);
    }

    [Fact]
    public void Duplicate_contraction_text_keeps_first_variable()
    {
        var c = DatisFormatter.BuildContractions([new("A", "SAME"), new("B", "SAME")]);
        Assert.Single(c);
        Assert.Equal("@A", c[0].Value);
    }

    [Fact]
    public void Invalid_remote_regex_does_not_throw()
    {
        var reps = new[] { new KeyValuePair<string, string>("(", "x") };
        var r = DatisFormatter.Format("KSFO", [Entry(Sample)], Now, reps);
        Assert.True(r.IsAvailable);
    }
}
