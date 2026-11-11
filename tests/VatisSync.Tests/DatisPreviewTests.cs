using VatisSync.Core;

namespace VatisSync.Tests;

public class DatisPreviewTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);

    private const string Sfo =
        "SFO ATIS INFO A 1156Z. 28010KT 10SM CLR. ILS RWY 28R IN USE. HAZD WX INFO FOR SFO AREA AVBL FM FSS. " +
        "CTC GND ON 121.65 FOR TAXI. NOTICE TO AIRMEN. TWY B CLSD. ...ADVS YOU HAVE INFO A.";

    private static DatisEntry Entry(string airport, string text, TimeSpan age, string type = "combined") =>
        new(airport, type, text, (Now - age).ToString("yyyy-MM-ddTHH:mm:ss.ffffff"));

    private sealed class FakeTime : TimeProvider
    {
        public DateTimeOffset Current { get; set; } = Now;
        public override DateTimeOffset GetUtcNow() => Current;
    }

    private sealed class FakeDatis(Func<DatisFetchResult> next) : IDatisSource
    {
        public int Calls;
        public Task<DatisFetchResult> FetchAsync(CancellationToken ct = default)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(next());
        }
    }

    private sealed class FakeReplacements(Dictionary<string, ReplacementRules>? rules = null) : IReplacementSource
    {
        public Task<IReadOnlyDictionary<string, ReplacementRules>> GetAsync(IEnumerable<string> airports, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyDictionary<string, ReplacementRules>>(rules ?? []);
    }

    private static AirportRules Defaults() => new([], new Dictionary<FrequencyRole, FrequencySetting>());

    private static (DatisPreviewBuilder Builder, FakeDatis Datis, FakeTime Time) Rig(
        Func<DatisFetchResult>? next = null, Dictionary<string, ReplacementRules>? local = null)
    {
        var time = new FakeTime();
        var datis = new FakeDatis(next ?? (() => new([Entry("KSFO", Sfo, TimeSpan.FromMinutes(10))], false, null)));
        return (new DatisPreviewBuilder(datis, new FakeReplacements(local), time), datis, time);
    }

    private static string Joined(IEnumerable<DiffSegment> segments, DiffKind kind) =>
        string.Join(" | ", segments.Where(s => s.Kind == kind).Select(s => s.Text));

    [Fact]
    public async Task Builds_the_text_for_vatis_and_shows_what_the_rules_took_out()
    {
        var (builder, _, _) = Rig();

        var station = Assert.Single((await builder.BuildAsync(["KSFO"], Defaults())).Stations);

        Assert.True(station.Available);
        Assert.Equal("ILS RWY 28R IN USE. CTC GND ON 121.65 FOR TAXI.", station.Conditions.Trim());
        Assert.Equal("TWY B CLSD.", station.Notams);
        Assert.Contains("HAZD WX INFO FOR SFO AREA AVBL FM FSS.", Joined(station.ConditionsDiff, DiffKind.Removed));
        Assert.Contains("ILS RWY 28R IN USE.", Joined(station.ConditionsDiff, DiffKind.Same));
        Assert.DoesNotContain(station.ConditionsDiff, s => s.Kind == DiffKind.Added);
    }

    [Fact]
    public async Task Rules_the_airport_switched_off_leave_the_text_alone()
    {
        var (builder, _, _) = Rig();
        var rules = new AirportRules(["flight-service-pointers"], new Dictionary<FrequencyRole, FrequencySetting>());

        var station = (await builder.BuildAsync(["KSFO"], rules)).Stations.Single();

        Assert.Contains("HAZD WX INFO FOR SFO AREA AVBL FM FSS", station.Conditions);
        Assert.DoesNotContain(station.ConditionsDiff, s => s.Kind != DiffKind.Same);
    }

    [Fact]
    public async Task A_replacement_frequency_shows_as_removed_and_added()
    {
        var (builder, _, _) = Rig();
        var rules = new AirportRules([], new Dictionary<FrequencyRole, FrequencySetting>
        {
            [FrequencyRole.Ground] = new(FrequencyMode.Override, "121.9"),
        });

        var station = (await builder.BuildAsync(["KSFO"], rules)).Stations.Single();

        Assert.Contains("CTC GND ON 121.9 FOR TAXI.", station.Conditions);
        Assert.Contains("121.65", Joined(station.ConditionsDiff, DiffKind.Removed));
        Assert.Contains("121.9", Joined(station.ConditionsDiff, DiffKind.Added));
    }

    [Fact]
    public async Task The_airports_own_replacement_rules_apply_too()
    {
        var (builder, _, _) = Rig(local: new() { ["KSFO"] = new List<KeyValuePair<string, string>> { new(@"\bTWY B CLSD\b", "TWY B CLOSED") } });

        var station = (await builder.BuildAsync(["KSFO"], Defaults())).Stations.Single();

        Assert.Equal("TWY B CLOSED", station.Notams); // a rule swallows the full stop after what it matches, as it always has
    }

    [Fact]
    public async Task Reads_the_information_letter_and_when_it_was_issued()
    {
        var (builder, _, time) = Rig();
        var station = (await builder.BuildAsync(["KSFO"], Defaults())).Stations.Single();

        Assert.Equal("A", station.Letter);
        Assert.Equal(Now - TimeSpan.FromMinutes(10), station.Issued);
        Assert.Equal("Information A, issued 10 minutes ago.", PreviewText.Source(station, time.Current));
    }

    [Theory]
    [InlineData(0, "just now")]
    [InlineData(1, "1 minute ago")]
    [InlineData(45, "45 minutes ago")]
    [InlineData(119, "119 minutes ago")]
    [InlineData(180, "3 hours ago")]
    public void Ages_are_written_in_plain_words(int minutes, string expected) =>
        Assert.Equal(expected, PreviewText.Age(Now - TimeSpan.FromMinutes(minutes), Now));

    [Fact]
    public async Task An_airport_with_no_real_world_data_is_unavailable_with_a_reason()
    {
        var (builder, _, _) = Rig();
        var station = (await builder.BuildAsync(["KXXX"], Defaults())).Stations.Single();

        Assert.False(station.Available);
        Assert.Contains("KXXX", station.Problem);
        Assert.Empty(station.Conditions);
    }

    [Fact]
    public async Task Data_that_is_too_old_is_not_used()
    {
        var (builder, _, _) = Rig(() => new([Entry("KSFO", Sfo, TimeSpan.FromHours(3))], false, null));
        var station = (await builder.BuildAsync(["KSFO"], Defaults())).Stations.Single();

        Assert.False(station.Available);
        Assert.Contains("too old", station.Problem);
        Assert.Equal("A", station.Letter); // still known, so the user can see which information it was
    }

    [Fact]
    public async Task Arrival_and_departure_stations_get_their_own_text()
    {
        var (builder, _, _) = Rig(() => new([
            Entry("KMIA", "MIA ARR INFO A 1156Z. W. ARR RWY 8L. NOTICE TO AIRMEN. A TWY CLSD. ...ADVS YOU HAVE INFO A.", TimeSpan.FromMinutes(5), "arr"),
            Entry("KMIA", "MIA DEP INFO B 1156Z. W. DEP RWY 9. NOTICE TO AIRMEN. B TWY CLSD. ...ADVS YOU HAVE INFO B.", TimeSpan.FromMinutes(5), "dep"),
        ], false, null));

        var result = await builder.BuildAsync(["KMIA_D", "KMIA_A"], Defaults());

        Assert.Equal(["KMIA_A", "KMIA_D"], result.Stations.Select(s => s.Station));
        Assert.Equal("ARR RWY 8L.", result.Stations[0].Conditions.Trim());
        Assert.Equal("DEP RWY 9.", result.Stations[1].Conditions.Trim());
        Assert.Equal(["A", "B"], result.Stations.Select(s => s.Letter));
    }

    [Fact]
    public async Task A_download_is_reused_for_a_minute_and_again_when_forced()
    {
        var (builder, datis, time) = Rig();

        await builder.BuildAsync(["KSFO"], Defaults());
        await builder.BuildAsync(["KSFO"], Defaults());
        Assert.Equal(1, datis.Calls);

        time.Current += DatisPreviewBuilder.CacheFor + TimeSpan.FromSeconds(1);
        await builder.BuildAsync(["KSFO"], Defaults());
        Assert.Equal(2, datis.Calls);

        await builder.BuildAsync(["KSFO"], Defaults(), forceRefresh: true);
        Assert.Equal(3, datis.Calls);
    }

    [Fact]
    public async Task Changing_the_airports_settings_reformats_without_downloading_again()
    {
        var (builder, datis, _) = Rig();
        var before = (await builder.BuildAsync(["KSFO"], Defaults())).Stations.Single();
        var after = (await builder.BuildAsync(["KSFO"], new AirportRules(["flight-service-pointers"], new Dictionary<FrequencyRole, FrequencySetting>()))).Stations.Single();

        Assert.Equal(1, datis.Calls);
        Assert.NotEqual(before.Conditions, after.Conditions);
    }

    [Fact]
    public async Task A_failed_download_still_shows_the_last_copy_and_says_why()
    {
        var calls = 0;
        var (builder, _, time) = Rig(() => ++calls == 1
            ? new([Entry("KSFO", Sfo, TimeSpan.FromMinutes(10))], false, null)
            : new([Entry("KSFO", Sfo, TimeSpan.FromMinutes(10))], FromCache: true, "atis.info returned HTTP 503"));

        await builder.BuildAsync(["KSFO"], Defaults());
        time.Current += DatisPreviewBuilder.CacheFor + TimeSpan.FromSeconds(1);
        var result = await builder.BuildAsync(["KSFO"], Defaults());

        Assert.True(result.FromCache);
        Assert.Equal("atis.info returned HTTP 503", result.FetchProblem);
        Assert.True(result.Stations.Single().Available);
    }

    [Fact]
    public async Task A_failed_download_with_no_copy_marks_stations_unavailable()
    {
        var (builder, datis, time) = Rig(() => new([], FromCache: false, "Could not reach atis.info"));

        var result = await builder.BuildAsync(["KSFO"], Defaults());
        Assert.Equal("Could not reach atis.info", result.FetchProblem);
        Assert.False(result.Stations.Single().Available);

        // A failure is retried soon, not after a minute.
        time.Current += DatisPreviewBuilder.FailureCacheFor + TimeSpan.FromSeconds(1);
        await builder.BuildAsync(["KSFO"], Defaults());
        Assert.Equal(2, datis.Calls);
    }

    // ---- the combined word comparison ----

    [Fact]
    public void Unified_diff_marks_removed_added_and_same_words_and_joins_neighbours()
    {
        var segments = WordDiff.Unified("A B C D E", "A X Y D E F");

        Assert.Equal(
            [("A", DiffKind.Same), ("B C", DiffKind.Removed), ("X Y", DiffKind.Added), ("D E", DiffKind.Same), ("F", DiffKind.Added)],
            segments.Select(s => (s.Text, s.Kind)));
    }

    [Fact]
    public void Unified_diff_of_identical_or_empty_texts_is_simple()
    {
        Assert.Equal([new DiffSegment("A B", DiffKind.Same)], WordDiff.Unified("A B", "A  B"));
        Assert.Empty(WordDiff.Unified("", null));
        Assert.Equal([new DiffSegment("A", DiffKind.Removed)], WordDiff.Unified("A", ""));
    }

    // ---- the discovery tells the window which stations each airport has ----

    [Fact]
    public async Task Discovery_reports_the_stations_it_read()
    {
        var found = new List<string>();
        var d = new ProfileDiscovery(_ => Task.FromResult<IReadOnlyList<VatisStation>>([
            new("1", "KMIA", "Arrival", ["D-ATIS"]), new("2", "KMIA", "Departure", ["D-ATIS"]), new("3", "KJFK", "Combined", [])]),
            TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(50));
        d.StationsFound += s => { lock (found) found.AddRange(s.Select(x => x.Key)); };

        d.Start();
        var until = DateTime.UtcNow.AddSeconds(5);
        while (d.State.Phase != DiscoveryPhase.Found && DateTime.UtcNow < until) await Task.Delay(10);
        await d.StopAsync();

        Assert.Equal(["KMIA_A", "KMIA_D"], found.Order()); // KJFK has no D-ATIS preset, so it is not one of them
    }
}
