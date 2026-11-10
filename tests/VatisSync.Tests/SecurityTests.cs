using System.Diagnostics;
using System.Net;
using System.Text;
using VatisSync.Core;

namespace VatisSync.Tests;

/// <summary>
/// What the security review found, kept fixed: each test states the safe behaviour for input the app does not control
/// (the internet feed, whatever answers on the vATIS port, hand-edited files).
/// </summary>
public class SecurityTests
{
    // ---- minimal fakes ----

    private sealed class Vatis : IVatisClient
    {
        private readonly object _l = new();
        public string Status = "Disconnected", Conditions = "", Notams = "";
        public int FailNextGetAtis;
        public int Configures;
        private static readonly VatisStation Station = new("id1", "KSFO", "Combined", ["D-ATIS"]);

        public Task<bool> WaitUntilReadyAsync(TimeSpan limit, CancellationToken ct = default) => Task.FromResult(true);
        public Task<IReadOnlyList<VatisStation>> GetStationsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<VatisStation>>([Station]);

        public Task<VatisAtisState> GetAtisAsync(string stationId, CancellationToken ct = default)
        {
            lock (_l)
            {
                if (FailNextGetAtis > 0)
                {
                    FailNextGetAtis--;
                    throw new VatisUnavailableException("vATIS did not answer getAtis for id1.");
                }
                return Task.FromResult(new VatisAtisState(Status, Conditions, Notams));
            }
        }

        public Task<IReadOnlyList<KeyValuePair<string, string>>> GetContractionsAsync(string stationKey, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<KeyValuePair<string, string>>>([]);

        public Task ConfigureAtisAsync(string stationId, string airportConditions, string notams, bool syncAtisLetter, CancellationToken ct = default)
        {
            lock (_l) { Conditions = airportConditions; Notams = notams; Configures++; }
            return Task.CompletedTask;
        }

        public Task<bool> ConnectAtisAsync(string stationId, CancellationToken ct = default)
        {
            lock (_l) Status = "Connected";
            return Task.FromResult(true);
        }

        public Task DisconnectAtisAsync(string stationId, CancellationToken ct = default) => Task.CompletedTask;
        public string Text { get { lock (_l) return Conditions; } }
    }

    private sealed class Datis : IDatisSource
    {
        public volatile IReadOnlyList<DatisEntry> Entries = [];
        public Task<DatisFetchResult> FetchAsync(CancellationToken ct = default) =>
            Task.FromResult(new DatisFetchResult(Entries, false, null));
    }

    private sealed class NoPosition : IActivePositionProvider { public CrcPosition? GetActivePosition() => null; }
    private sealed class Launcher : IVatisLauncher { public LaunchResult EnsureRunning() => LaunchResult.AlreadyRunning; }
    private sealed class FixedTime(DateTimeOffset now) : TimeProvider { public override DateTimeOffset GetUtcNow() => now; }

    private sealed class Approver(ApprovalDecision answer) : IUpdateApprover
    {
        public int Asked;
        public Task<ApprovalDecision> RequestAsync(ApprovalRequest request, CancellationToken ct)
        {
            Interlocked.Increment(ref Asked);
            return Task.FromResult(answer);
        }
    }

    private static readonly DateTimeOffset Now = new(2026, 11, 7, 18, 0, 0, TimeSpan.Zero);

    private static DatisEntry Entry(string conditions) => new("KSFO", "combined",
        "SFO ATIS INFO A 1156Z. 28010KT 10SM CLR. " + conditions + " NOTICE TO AIRMEN. TWY C CLSD. ...ADVS YOU HAVE INFO A.",
        Now.AddMinutes(-10).ToString("yyyy-MM-ddTHH:mm:ss.ffffff"));

    private static async Task WaitFor(Func<bool> condition, int timeoutMs = 5000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition() && DateTime.UtcNow < until) await Task.Delay(5);
    }

    // ---- 1. approval gate ----

    private static SyncEngine Engine(Vatis vatis, Datis datis, Approver approver) =>
        new(vatis, datis, new NoReplacements(), new NoPosition(), new Launcher(),
            new SyncOptions
            {
                Airports = ["KSFO"],
                Interval = TimeSpan.FromMilliseconds(40),
                PostConnectDelay = TimeSpan.Zero,
                RetryDelay = TimeSpan.FromMilliseconds(30),
                StartupTimeout = TimeSpan.FromSeconds(1),
                Approval = ApprovalMode.RunwayChanges,
            }, new FixedTime(Now), approver);

    /// <summary>Started and connected, with runway 28R in vATIS.</summary>
    private static async Task<(SyncEngine Engine, Vatis Vatis, Datis Datis)> StartedOn28R(Approver approver)
    {
        var vatis = new Vatis();
        var datis = new Datis { Entries = [Entry("ILS RWY 28R IN USE.")] };
        var engine = Engine(vatis, datis, approver);
        engine.Start();
        await WaitFor(() => engine.Status.Phase == SyncPhase.Idle);
        Assert.Contains("RWY 28R", vatis.Text);
        Assert.Equal("Connected", vatis.Status);
        return (engine, vatis, datis);
    }

    [Fact]
    public async Task A_vatis_hiccup_must_not_let_a_runway_change_through_without_approval()
    {
        var approver = new Approver(ApprovalDecision.Disregard);
        var (engine, vatis, datis) = await StartedOn28R(approver);

        // The real world switches runways, and vATIS misses one reply (its timeout is one second), so the engine
        // starts over by itself.
        vatis.FailNextGetAtis = 1;
        datis.Entries = [Entry("ILS RWY 10L IN USE.")];
        await WaitFor(() => approver.Asked > 0);
        await Task.Delay(300);
        await engine.StopAsync();

        Assert.Contains("RWY 28R", vatis.Text); // the controller said Disregard, so 28R is still there
        Assert.Equal(1, approver.Asked);        // and they were asked, once
    }

    [Fact]
    public async Task After_a_hiccup_an_approved_runway_change_is_applied()
    {
        var approver = new Approver(ApprovalDecision.Apply);
        var (engine, vatis, datis) = await StartedOn28R(approver);

        vatis.FailNextGetAtis = 1;
        datis.Entries = [Entry("ILS RWY 10L IN USE.")];
        await WaitFor(() => vatis.Text.Contains("RWY 10L"));
        await engine.StopAsync();

        Assert.Contains("RWY 10L", vatis.Text);
        Assert.Equal(1, approver.Asked);
    }

    [Fact]
    public async Task A_hiccup_does_not_forget_what_the_controller_disregarded()
    {
        var approver = new Approver(ApprovalDecision.DisregardForSession);
        var (engine, vatis, datis) = await StartedOn28R(approver);

        datis.Entries = [Entry("ILS RWY 10L IN USE.")];
        await WaitFor(() => approver.Asked == 1);

        // Several hiccups later the airport is still disregarded: not asked again, not written.
        for (var i = 0; i < 3; i++)
        {
            vatis.FailNextGetAtis = 1;
            await Task.Delay(200);
        }
        await engine.StopAsync();

        Assert.Contains("RWY 28R", vatis.Text);
        Assert.Equal(1, approver.Asked);
    }

    [Fact]
    public async Task After_a_hiccup_a_connected_atis_is_not_replaced_by_not_available()
    {
        var approver = new Approver(ApprovalDecision.Apply);
        var (engine, vatis, datis) = await StartedOn28R(approver);

        vatis.FailNextGetAtis = 1;
        datis.Entries = []; // the feed has nothing for the airport right now
        await Task.Delay(400);
        await engine.StopAsync();

        Assert.Contains("RWY 28R", vatis.Text);
        Assert.Equal(0, approver.Asked);
    }

    [Fact]
    public async Task Pressing_Start_again_still_fills_without_asking()
    {
        var approver = new Approver(ApprovalDecision.Disregard);
        var (engine, vatis, datis) = await StartedOn28R(approver);
        await engine.StopAsync();

        datis.Entries = [Entry("ILS RWY 10L IN USE.")];
        engine.Start();
        await WaitFor(() => vatis.Text.Contains("RWY 10L"));
        await engine.StopAsync();

        Assert.Contains("RWY 10L", vatis.Text); // the first fill after Start is what the controller asked for
        Assert.Equal(0, approver.Asked);
    }

    [Theory]
    [InlineData("RWYS 9, 27 CLSD, EXPECT DELAYS.")]
    [InlineData("RWY 28L CLSD BTN TWY A AND TWY B, EXPECT DELAYS.")]
    [InlineData("ILS RWY 28L OTS.")]
    public void A_sentence_that_is_only_about_a_closure_is_still_not_a_flow(string text) =>
        Assert.True(FlowAnalyzer.Analyze(text).Runways.Count == 0, FlowAnalyzer.Analyze(text).Describe());

    [Fact]
    public void A_closed_runway_beside_the_flow_is_left_out_of_it()
    {
        var flow = FlowAnalyzer.Analyze("LNDG AND DEPG RWY 9, RWY 27 CLSD.");
        Assert.Equal(["9"], flow.Runways);
    }

    [Fact]
    public void An_absurdly_long_atis_is_left_out_of_the_list()
    {
        var json = "[{\"airport\":\"KSFO\",\"type\":\"combined\",\"datis\":\"" + new string('A', DatisFetcher.MaxDatisChars + 1) + "\"}," +
                   "{\"airport\":\"KOAK\",\"type\":\"combined\",\"datis\":\"OAK ATIS INFO A.\"}]";
        var entries = DatisFetcher.Parse(json, out _);
        Assert.Equal(["KOAK"], entries!.Select(e => e.Airport));
    }

    [Theory]
    [InlineData("LNDG AND DEPG RWY 9, RWY 27 CLSD.", "LNDG AND DEPG RWY 27, RWY 9 CLSD.")]
    [InlineData("ARRIVALS EXPECT ILS RWY 28R, RWY 28L CLSD.", "ARRIVALS EXPECT ILS RWY 10L, RWY 10R CLSD.")]
    [InlineData("SIMUL APCHS IN USE RWYS 26 AND 27, BRAKING ACTION ADVISORIES IN EFFECT.", "SIMUL APCHS IN USE RWYS 8 AND 9, BRAKING ACTION ADVISORIES IN EFFECT.")]
    public void A_runway_change_is_seen_even_when_the_sentence_also_mentions_a_closure(string before, string after) =>
        Assert.True(FlowAnalyzer.FlowChanged(before, after));

    // ---- 2. hostile text from the feed ----

    private static readonly Dictionary<FrequencyRole, FrequencySetting> RemoveGround =
        new() { [FrequencyRole.Ground] = new FrequencySetting(FrequencyMode.Remove) };

    [Fact]
    public void A_pathological_feed_text_does_not_throw_out_of_the_formatter()
    {
        var hostile = "SFO ATIS INFO A 1156Z. 28010KT. ILS RWY 28R IN USE. " + new string('.', 300_000) + "X GND 121.9.";
        var entries = new[] { new DatisEntry("KSFO", "combined", hostile, null) };
        var ex = Record.Exception(() => DatisFormatter.Format("KSFO", entries, Now, BuiltInRules.PatternsFor(), null, RemoveGround));
        Assert.Null(ex);
    }

    [Fact]
    public void A_huge_feed_text_is_formatted_quickly_or_refused()
    {
        var hostile = "SFO ATIS INFO A 1156Z. 28010KT. " + string.Concat(Enumerable.Repeat("RWY 28R CLSD BTN TWY A AND TWY B, ", 60_000));
        var entries = new[] { new DatisEntry("KSFO", "combined", hostile, null) };
        var sw = Stopwatch.StartNew();
        var ex = Record.Exception(() => DatisFormatter.Format("KSFO", entries, Now, BuiltInRules.PatternsFor(), null, RemoveGround));
        sw.Stop();
        Assert.Null(ex);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"formatting took {sw.Elapsed.TotalSeconds:0.0} s for {hostile.Length} chars");
    }

    private sealed class Handler(Func<HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(respond());
    }

    [Fact]
    public async Task An_oversized_response_is_refused()
    {
        var body = "[{\"airport\":\"KSFO\",\"type\":\"combined\",\"datis\":\"" + new string('A', 30_000_000) + "\"}]";
        using var http = new HttpClient(new Handler(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) }));
        var result = await new DatisFetcher(http).FetchAsync();
        Assert.False(result.Succeeded);
    }

    [Fact]
    public void Control_characters_from_the_feed_do_not_reach_vatis()
    {
        var entries = new[] { new DatisEntry("KSFO", "combined", "SFO ATIS INFO A 1156Z. 28010KT. ILS RWY 28R\u0000 IN\u001b[2J USE" + (char)0x202E + ". TWY B CLSD.", null) };
        var text = DatisFormatter.Format("KSFO", entries, Now).AirportConditions;
        Assert.DoesNotContain(text, c => char.IsControl(c) || c == (char)0x202E);
    }

    // ---- 3. replies from whatever is listening on the vATIS port ----

    private sealed class Scripted(params string[] replies) : IVatisSocketFactory
    {
        public Task<IVatisSocket> ConnectAsync(CancellationToken ct) => Task.FromResult<IVatisSocket>(new Socket(replies));

        private sealed class Socket(string[] replies) : IVatisSocket
        {
            private readonly Queue<string> _q = new(replies);
            public Task SendAsync(string text, CancellationToken ct) => Task.CompletedTask;
            public Task<string?> ReceiveAsync(TimeSpan timeout, CancellationToken ct) => Task.FromResult(_q.Count > 0 ? _q.Dequeue() : null);
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    [Theory]
    [InlineData("""{"value":{"networkConnectionStatus":5,"airportConditions":{},"notams":[]}}""")]
    public async Task A_wrongly_typed_getAtis_reply_is_a_vatis_problem_not_a_crash(string reply)
    {
        var ex = await Record.ExceptionAsync(() => new VatisClient(new Scripted(reply)).GetAtisAsync("id1"));
        Assert.True(ex is null or VatisUnavailableException, ex?.GetType().Name);
    }

    [Theory]
    [InlineData("""{"stations":"nope"}""")]
    [InlineData("""{"stations":[{"contractions":{"RWY":"RUNWAY"}}]}""")]
    [InlineData("""{"stations":[{"contractions":{"RWY":{"text":5}}}]}""")]
    public async Task A_wrongly_typed_contractions_reply_is_a_vatis_problem_not_a_crash(string reply)
    {
        var ex = await Record.ExceptionAsync(() => new VatisClient(new Scripted(reply)).GetContractionsAsync("KSFO"));
        Assert.True(ex is null or VatisUnavailableException, ex?.GetType().Name);
    }

    [Theory]
    [InlineData("""{"type":"stations","stations":["x", 5, {"id":7,"name":"KSFO"}, {"id":"a","name":"KSFO","presets":"D-ATIS"}]}""")]
    public async Task A_wrongly_typed_stations_reply_is_a_vatis_problem_not_a_crash(string reply)
    {
        var ex = await Record.ExceptionAsync(() => new VatisClient(new Scripted(reply, reply, reply, reply, reply)).GetStationsAsync());
        Assert.True(ex is null or VatisUnavailableException, ex?.GetType().Name);
    }

    // ---- 4. files ----

    [Theory]
    [InlineData("KSFO\n")]
    [InlineData("..\\X")]
    [InlineData("K/FO")]
    [InlineData("CON1")]
    public void Odd_airport_ids_are_not_accepted_as_file_names(string icao)
    {
        // CON1 is a legal 4-character id but not a reserved device name (those are CON, COM1...), so it is allowed.
        if (icao == "CON1") { Assert.True(AirportFileStore.IsIcao(icao)); return; }
        Assert.False(AirportFileStore.IsIcao(icao));
    }

    // ---- 5. logs ----

    [Fact]
    public void A_message_cannot_forge_a_second_log_entry()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vatis-probe-" + Guid.NewGuid().ToString("N"));
        try
        {
            var log = new ActivityLog(dir);
            log.Write("atis.info error: nothing to see" + (char)13 + "2026-11-07 18:00:00.000  OK     KSFO: update approved" + (char)0x2028 + "second" + (char)0x85 + "third");
            var text = File.ReadAllText(log.TodaysFile, Encoding.UTF8).TrimEnd('\r', '\n');
            Assert.DoesNotContain('\r', text.Replace("\r\n", "\n"));
            Assert.DoesNotContain((char)0x2028, text);
            Assert.DoesNotContain('\u0085', text);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }
}
