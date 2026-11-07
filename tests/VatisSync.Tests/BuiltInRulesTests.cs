using System.Text.Json;
using System.Text.RegularExpressions;
using VatisSync.Core;
using Xunit.Abstractions;

namespace VatisSync.Tests;

public class BuiltInRulesTests(ITestOutputHelper output)
{
    private static readonly List<DatisEntry> Entries = JsonSerializer.Deserialize<List<DatisEntry>>(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "atis-info-all-2026-11-07.json")),
        new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

    private static readonly DateTimeOffset Now = Entries
        .Select(e => DateTimeOffset.Parse(e.UpdatedAt![..26], null, System.Globalization.DateTimeStyles.AssumeUniversal))
        .Max().AddMinutes(1);

    private static string Station(DatisEntry e) => e.Type switch
    { "arr" => e.Airport + "_A", "dep" => e.Airport + "_D", _ => e.Airport };

    private static string[] Sentences(FormattedAtis f) =>
        Regex.Split((f.AirportConditions + " " + f.Notams).Trim(), @"(?<=\.)\s+")
            .Select(s => s.Trim()).Where(s => s.Length > 0).ToArray();

    private static ReplacementRules Only(string id) =>
        BuiltInRules.PatternsFor(BuiltInRules.All.Select(r => r.Id).Where(i => i != id));

    /// <summary>(airport/type, sentence) pairs that disappeared, and ones that appeared, when applying the rules.</summary>
    private static (List<string> Removed, List<string> Added) Diff(ReplacementRules rules)
    {
        var removed = new List<string>(); var added = new List<string>();
        foreach (var e in Entries)
        {
            var before = Sentences(DatisFormatter.Format(Station(e), Entries, Now));
            var after = Sentences(DatisFormatter.Format(Station(e), Entries, Now, rules));
            removed.AddRange(before.Except(after).Select(s => $"{e.Airport}/{e.Type}: {s}"));
            added.AddRange(after.Except(before).Select(s => $"{e.Airport}/{e.Type}: {s}"));
        }
        return (removed, added);
    }

    [Fact]
    public void Embedded_rules_load_with_unique_ids_and_valid_regexes()
    {
        Assert.Equal(["flight-service-pointers", "drone-laser-reports", "tfr-notam-pointers"],
            BuiltInRules.All.Select(r => r.Id));
        foreach (var r in BuiltInRules.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(r.Name));
            Assert.NotEmpty(r.Patterns);
        }
        foreach (var (pattern, _) in BuiltInRules.PatternsFor())
            _ = new Regex(pattern); // throws if a pattern does not compile
    }

    [Fact]
    public void Disabled_ids_are_left_out_case_insensitively_and_unknown_ids_are_ignored()
    {
        var all = BuiltInRules.PatternsFor().Count;
        Assert.Equal(all - 1, BuiltInRules.PatternsFor(["DRONE-LASER-REPORTS"]).Count);
        Assert.Equal(all, BuiltInRules.PatternsFor(["no-such-rule"]).Count);
        Assert.Empty(BuiltInRules.PatternsFor(BuiltInRules.All.Select(r => r.Id)));
    }

    [Fact]
    public void Parse_allows_comments_and_expands_the_sentence_placeholder()
    {
        var rules = BuiltInRules.Parse(
            "{ // c\n \"rules\":[{\"id\":\"x\",\"name\":\"X\",\"patterns\":[\"{S}FOO{S}\"],},] }");
        var pattern = BuiltInRules.PatternsFor(rules, []).Single().Key;
        Assert.DoesNotContain("{S}", pattern);
        Assert.Matches(pattern, "SOMETHING FOO ELSE");
    }

    [Theory]
    [InlineData("flight-service-pointers", "HAZD WX INFO FOR LAX AREA AVBL FM FSS")]
    [InlineData("drone-laser-reports", "LASER ACTIVITY REPORTED NR RWY 4")]
    [InlineData("tfr-notam-pointers", "TFR IN EFFECT WITHIN 30 NM")]
    public void A_rule_removes_its_sentence_and_keeps_the_neighbours(string id, string target)
    {
        var entry = new DatisEntry("KTST", "combined",
            $"H1. H2. KEEP ONE. {target}. KEEP TWO. ...ADVS YOU HAVE INFO A.",
            Now.ToString("yyyy-MM-ddTHH:mm:ss.ffffff"));
        var text = DatisFormatter.Format("KTST", [entry], Now, Only(id)).AirportConditions;
        Assert.Equal("KEEP ONE. KEEP TWO.", text.Trim());
    }

    [Theory]
    [InlineData("flight-service-pointers", 15)]
    [InlineData("drone-laser-reports", 6)]
    [InlineData("tfr-notam-pointers", 2)]
    public void Real_data_each_rule_only_removes_sentences_never_edits_them(string id, int expectedRemoved)
    {
        // Golden counts over the 88-entry capture: a change here means a rule now touches different text,
        // so review the logged sentences before updating the number.
        var (removed, added) = Diff(Only(id));
        removed.ForEach(s => output.WriteLine("- " + s));
        Assert.Empty(added);
        Assert.Equal(expectedRemoved, removed.Count);
    }

    [Fact]
    public void Rules_never_leave_broken_punctuation_or_lose_the_notam_split()
    {
        foreach (var e in Entries)
        {
            var before = DatisFormatter.Format(Station(e), Entries, Now);
            var after = DatisFormatter.Format(Station(e), Entries, Now, BuiltInRules.PatternsFor());
            if (!before.IsAvailable) continue;
            var label = $"{e.Airport}/{e.Type}";
            Assert.DoesNotContain("....", after.AirportConditions + after.Notams);
            Assert.False(after.AirportConditions.Contains("NOTAMS..."), $"{label}: split marker left in conditions");
            // A rule may empty the NOTAM section, but must not turn NOTAMs into conditions or vice versa.
            Assert.True(before.Notams.Length > 0 || after.Notams.Length == 0, $"{label}: NOTAMs appeared from nowhere");
        }
    }
}
