using VatisSync.Core;

namespace VatisSync.Tests;

public class FlowAnalyzerTests
{
    private static FlowSignature Flow(string text) => FlowAnalyzer.Analyze(text);

    [Fact]
    public void Reads_runways_and_approach_types_from_a_real_phx_text()
    {
        // KPHX as it appears in vATIS, with contractions.
        var f = Flow("@SIMUL @APCHS TO @PARL RWYS IN USE. USE @CTN FOR ACFT ON @PARL FINAL @APCHS, DO NOT OVERSHOOT FINAL. " +
                     "EXPECT ILS OR VISUAL @APCH @RWY 8, ILS OR VISUAL @APCH @RWY 7R. DEPARTING @RWY 7L.");
        Assert.Equal(["7L", "7R", "8"], f.Runways);
        Assert.Equal(["ILS", "VISUAL"], f.Approaches);
    }

    [Theory]
    [InlineData("ILS RWY 28R IN USE.", "28R")]
    [InlineData("LNDG AND DEPG RWYS 31.", "31")]
    [InlineData("SIMUL INSTR DEPARTURES IN PROG RWYS 24 AND 25.", "24,25")]
    [InlineData("LANDING RUNWAYS 7L, 7R, AND 8 IN USE.", "7L,7R,8")]
    [InlineData("EXPECT RNAV RY 24R APCH.", "24R")]
    [InlineData("DEPG RWY 19L / 19R.", "19L,19R")]
    [InlineData("SIMUL DEPS, DEPG RWYS, 26L, 27R.", "26L,27R")]       // KATL dep
    [InlineData("XPECT RNAV OFF THE GROUND DEPTG RWY 26L.", "26L")]
    [InlineData("SIMULTANEOUS APCHS IN USE VIS 26R, VIS 27L, VIS 28.", "26R,27L,28")] // KATL arr
    [InlineData("ILS 4R, DEP 4R.", "4R")]                              // KBOS
    [InlineData("SIMUL DEPS IN USE,  EXPECT 1R, 36.", "1R,36")]        // KCLT
    [InlineData("DEPG RWY8, RWY17L, RWY25.", "17L,25,8")]              // KDEN, no space after RWY
    public void Recognizes_the_ways_runways_are_written(string text, string expected) =>
        Assert.Equal(expected.Split(','), Flow(text).Runways);

    [Fact]
    public void Ignores_runway_closures_hold_short_and_equipment_sentences()
    {
        var f = Flow("ILS RWY 28R IN USE. RWY 22L EEMASS OTS. RWY 4 CLSD. HOLD SHORT OF RWY 28L. TWY B CLSD BTN RWY 10 AND RWY 12.");
        Assert.Equal(["28R"], f.Runways);
    }

    [Fact]
    public void A_quantity_after_a_flow_word_is_not_a_runway()
    {
        Assert.Empty(Flow("EXPECT 30 MIN DELAYS FOR DEPARTURES.").Runways);
        Assert.Empty(Flow("EXPECT 15 KT CROSSWIND ON APCH.").Runways);
    }

    [Fact]
    public void Order_and_duplicates_do_not_matter()
    {
        Assert.True(Flow("DEPG RWY 7L. LNDG RWY 8, RWY 7R.").SameAs(Flow("LNDG RWYS 7R, 8. DEPARTING RWY 7L. DEPG RWY 7L.")));
    }

    [Fact]
    public void Text_without_a_flow_has_an_empty_signature()
    {
        Assert.True(Flow("BIRD ACTIVITY VICINITY ARPT. READBACK HOLD SHORT INSTRUCTIONS.").IsEmpty);
        Assert.True(Flow("").IsEmpty);
        Assert.True(FlowAnalyzer.Analyze(null).IsEmpty);
    }

    [Theory]
    [InlineData("ILS RWY 28R IN USE.", "ILS RWY 10L IN USE.", true)]   // different runway
    [InlineData("ILS RWY 28R IN USE.", "VISUAL RWY 28R IN USE.", true)] // different approach type
    [InlineData("ILS RWY 28R IN USE.", "ILS RWY 28R IN USE. TWY B CLSD.", false)] // unrelated addition
    [InlineData("ILS RWY 28R IN USE. TWY B CLSD.", "ILS RWY 28R IN USE.", false)]
    [InlineData("LNDG RWY 28R.", "LNDG RWYS 28R, 28L.", true)]         // extra runway
    public void Detects_flow_changes(string current, string updated, bool changed) =>
        Assert.Equal(changed, FlowAnalyzer.FlowChanged(current, updated));

    [Fact]
    public void Every_real_atis_in_the_capture_yields_a_flow()
    {
        var entries = System.Text.Json.JsonSerializer.Deserialize<List<DatisEntry>>(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "atis-info-all-2026-11-07.json")),
            new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        var now = entries.Select(e => DateTimeOffset.Parse(e.UpdatedAt![..26], null,
            System.Globalization.DateTimeStyles.AssumeUniversal)).Max().AddMinutes(1);

        var empty = new List<string>();
        var total = 0;
        foreach (var e in entries)
        {
            var station = e.Type switch { "arr" => e.Airport + "_A", "dep" => e.Airport + "_D", _ => e.Airport };
            var text = DatisFormatter.Format(station, entries, now);
            if (!text.IsAvailable) continue;
            total++;
            // The approval check reads the Airport Conditions field, so that is where the flow must be found.
            if (FlowAnalyzer.Analyze(text.AirportConditions).IsEmpty)
                empty.Add($"{e.Airport}/{e.Type}");
        }

        Assert.True(empty.Count <= 0,
            $"{empty.Count} of {total} texts gave no flow: {string.Join(", ", empty)}");
    }

    [Fact]
    public void Describes_the_flow_for_the_prompt()
    {
        Assert.Equal("runways 28R; ILS", Flow("ILS RWY 28R IN USE.").Describe());
        Assert.Equal("no runways or approaches", FlowSignature.Empty.Describe());
    }
}
