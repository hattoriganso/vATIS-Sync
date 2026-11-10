using VatisSync.Core;

namespace VatisSync.Tests;

public class SyncEngineTests
{
    // ---- fakes ----

    private sealed record ConfigureCall(string Id, string Conditions, string Notams, bool Sync);

    private sealed class StationState
    {
        public string Status = "Disconnected";
        public string Conditions = "";
        public string Notams = "";
    }

    /// <summary>A tiny in-memory vATIS: remembers what was configured, connected and disconnected.</summary>
    private sealed class FakeVatis : IVatisClient
    {
        private readonly object _l = new();
        private readonly Dictionary<string, StationState> _states = [];
        private readonly List<ConfigureCall> _configures = [];
        private readonly List<string> _connects = [], _disconnects = [];

        public List<VatisStation> Stations { get; } = [];
        public Dictionary<string, List<KeyValuePair<string, string>>> Contractions { get; } = [];
        public bool Down { get; set; }
        public bool ReplyToConnect { get; set; } = true;

        public void Add(string name, string atisType = "Combined", string status = "Disconnected", string preset = "D-ATIS")
        {
            var s = new VatisStation("id-" + name + atisType, name, atisType, [preset]);
            lock (_l) { Stations.Add(s); _states[s.Id] = new StationState { Status = status }; }
        }

        public string IdOf(string key) => Stations.Single(s => s.Key == key).Id;
        public void Edit(string key, Action<StationState> change) { lock (_l) change(_states[IdOf(key)]); }
        public StationState StateOf(string key) { lock (_l) return _states[IdOf(key)]; }
        public List<ConfigureCall> Configures(string key) { lock (_l) return _configures.Where(c => c.Id == IdOf(key)).ToList(); }
        public List<ConfigureCall> AllConfigures() { lock (_l) return [.. _configures]; }
        public List<string> Connects() { lock (_l) return [.. _connects]; }
        public List<string> Disconnects() { lock (_l) return [.. _disconnects]; }

        private void Check() { if (Down) throw new VatisUnavailableException("down"); }

        public Task<bool> WaitUntilReadyAsync(TimeSpan limit, CancellationToken ct = default) => Task.FromResult(!Down);

        public Task<IReadOnlyList<VatisStation>> GetStationsAsync(CancellationToken ct = default)
        {
            Check();
            lock (_l) return Task.FromResult<IReadOnlyList<VatisStation>>([.. Stations]);
        }

        public Task<VatisAtisState> GetAtisAsync(string stationId, CancellationToken ct = default)
        {
            Check();
            lock (_l)
            {
                var s = _states[stationId];
                return Task.FromResult(new VatisAtisState(s.Status, s.Conditions, s.Notams));
            }
        }

        public Task<IReadOnlyList<KeyValuePair<string, string>>> GetContractionsAsync(string stationKey, CancellationToken ct = default)
        {
            Check();
            lock (_l)
                return Task.FromResult<IReadOnlyList<KeyValuePair<string, string>>>(
                    Contractions.TryGetValue(stationKey, out var c) ? [.. c] : []);
        }

        public Task ConfigureAtisAsync(string id, string conditions, string notams, bool sync, CancellationToken ct = default)
        {
            Check();
            lock (_l)
            {
                _configures.Add(new(id, conditions, notams, sync));
                _states[id].Conditions = conditions;
                _states[id].Notams = notams;
            }
            return Task.CompletedTask;
        }

        public Task<bool> ConnectAtisAsync(string id, CancellationToken ct = default)
        {
            Check();
            lock (_l) { _connects.Add(Stations.Single(s => s.Id == id).Key); _states[id].Status = "Connected"; }
            return Task.FromResult(ReplyToConnect);
        }

        public Task DisconnectAtisAsync(string id, CancellationToken ct = default)
        {
            Check();
            lock (_l) { _disconnects.Add(Stations.Single(s => s.Id == id).Key); _states[id].Status = "Disconnected"; }
            return Task.CompletedTask;
        }
    }

    private sealed class FakeDatis : IDatisSource
    {
        public volatile IReadOnlyList<DatisEntry> Entries = [];
        public Task<DatisFetchResult> FetchAsync(CancellationToken ct = default) =>
            Task.FromResult(new DatisFetchResult(Entries, false, null));
    }

    private sealed class FakePosition(CrcPosition? pos) : IActivePositionProvider
    {
        public CrcPosition? GetActivePosition() => pos;
    }

    private sealed class FakeLauncher(LaunchResult result = LaunchResult.AlreadyRunning) : IVatisLauncher
    {
        public int Calls;
        public LaunchResult EnsureRunning() { Calls++; return result; }
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    // ---- scaffolding ----

    private static readonly DateTimeOffset Now = new(2026, 11, 7, 18, 0, 0, TimeSpan.Zero);

    private static DatisEntry Entry(string icao, string type = "combined") => new(icao, type,
        icao[1..] + " ATIS INFO A 1156Z. 28010KT 10SM CLR. ILS RWY 28R IN USE. TWY B CLSD. " +
        "NOTICE TO AIRMEN. TWY C CLSD. ...ADVS YOU HAVE INFO A.",
        Now.AddMinutes(-10).ToString("yyyy-MM-ddTHH:mm:ss.ffffff"));

    private sealed class FakeApprover(Func<ApprovalRequest, ApprovalDecision>? decide = null) : IUpdateApprover
    {
        private readonly object _l = new();
        private readonly List<ApprovalRequest> _requests = [];

        public Func<ApprovalRequest, ApprovalDecision> Decide { get; set; } = decide ?? (_ => ApprovalDecision.Apply);
        public TaskCompletionSource? Block { get; set; }
        public List<ApprovalRequest> Requests { get { lock (_l) return [.. _requests]; } }

        public async Task<ApprovalDecision> RequestAsync(ApprovalRequest request, CancellationToken ct)
        {
            lock (_l) _requests.Add(request);
            if (Block is { } b) await b.Task.WaitAsync(ct);
            return Decide(request);
        }
    }

    private sealed class Rig
    {
        public FakeVatis Vatis { get; } = new();
        public FakeDatis Datis { get; } = new();
        public FakeLauncher Launcher { get; init; } = new();
        public SyncEngine Engine { get; }
        public List<string> Logs { get; } = [];

        public Rig(SyncOptions? options = null, CrcPosition? position = null, IUpdateApprover? approver = null)
        {
            options ??= Fast();
            Engine = new SyncEngine(Vatis, Datis, new NoReplacements(), new FakePosition(position),
                Launcher, options, new FixedTime(Now), approver);
            Engine.Log += m => { lock (Logs) Logs.Add(m); };
        }

        public static SyncOptions Fast() => new()
        {
            // Connecting requires a selection (or a followed position), so most tests select all their airports.
            Airports = ["KSFO", "KOAK", "KSJC", "KATL"],
            Interval = TimeSpan.FromMilliseconds(250),
            PostConnectDelay = TimeSpan.Zero,
            RetryDelay = TimeSpan.FromMilliseconds(30),
            StartupTimeout = TimeSpan.FromSeconds(1),
        };
    }

    private static async Task WaitFor(Func<bool> condition, string what, int timeoutMs = 5000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > until) throw new TimeoutException("Timed out waiting for: " + what);
            await Task.Delay(5);
        }
    }

    private static Task WaitIdle(Rig r) => WaitFor(() => r.Engine.Status.Phase == SyncPhase.Idle, "engine idle");

    // ---- tests ----

    // ---- approval of flow changes ----

    private static DatisEntry Conditions(string icao, string conditions) => new(icao, "combined",
        icao[1..] + " ATIS INFO A 1156Z. 28010KT 10SM CLR. " + conditions +
        " NOTICE TO AIRMEN. TWY C CLSD. ...ADVS YOU HAVE INFO A.",
        Now.AddMinutes(-10).ToString("yyyy-MM-ddTHH:mm:ss.ffffff"));

    private static Rig ApprovalRig(FakeApprover approver, ApprovalMode mode = ApprovalMode.RunwayChanges) =>
        new(Rig.Fast() with { Approval = mode, Interval = TimeSpan.FromMilliseconds(40) }, approver: approver);

    private static async Task StartedWith(Rig r, string conditions)
    {
        r.Vatis.Add("KSFO");
        r.Datis.Entries = [Conditions("KSFO", conditions)];
        r.Engine.Start();
        await WaitIdle(r);
    }

    // An airport with an arrival and a departure station (KSFO_A, KSFO_D), both connected.
    private static DatisEntry ConditionsOf(string icao, string type, string conditions) =>
        Conditions(icao, conditions) with { Type = type };

    private static async Task StartedWithBoth(Rig r, string arrival, string departure)
    {
        r.Vatis.Add("KSFO", "Arrival");
        r.Vatis.Add("KSFO", "Departure");
        r.Datis.Entries = [ConditionsOf("KSFO", "arr", arrival), ConditionsOf("KSFO", "dep", departure)];
        r.Engine.Start();
        await WaitIdle(r);
    }

    private static void BothNow(Rig r, string arrival, string departure) =>
        r.Datis.Entries = [ConditionsOf("KSFO", "arr", arrival), ConditionsOf("KSFO", "dep", departure)];

    private static Task WaitForBoth(Rig r, string text) =>
        WaitFor(() => r.Vatis.StateOf("KSFO_A").Conditions.Contains(text) && r.Vatis.StateOf("KSFO_D").Conditions.Contains(text),
            $"{text} to reach both stations");

    private static bool Desynced(Rig r, string key) => r.Engine.Status.Stations.Single(s => s.Key == key).Desynced;

    private static Task WaitForText(Rig r, string text) =>
        WaitFor(() => r.Vatis.StateOf("KSFO").Conditions.Contains(text), $"{text} to reach vATIS");

    [Fact]
    public async Task Taxiway_change_with_the_same_runways_is_applied_without_asking()
    {
        var approver = new FakeApprover();
        var r = ApprovalRig(approver);
        await StartedWith(r, "ILS RWY 28R IN USE. TWY B CLSD.");

        r.Datis.Entries = [Conditions("KSFO", "ILS RWY 28R IN USE. TWY D CLSD.")];
        await WaitForText(r, "TWY D CLSD");
        await r.Engine.StopAsync();

        Assert.Empty(approver.Requests);
    }

    [Fact]
    public async Task Runway_change_asks_and_applies_when_approved()
    {
        var approver = new FakeApprover();
        var r = ApprovalRig(approver);
        await StartedWith(r, "ILS RWY 28R IN USE.");

        r.Datis.Entries = [Conditions("KSFO", "ILS RWY 10L IN USE.")];
        await WaitForText(r, "RWY 10L");
        await r.Engine.StopAsync();

        var request = Assert.Single(approver.Requests);
        Assert.Equal("KSFO", request.Airport);
        var change = Assert.Single(request.Changes);
        Assert.True(change.FlowChanged);
        Assert.Equal("KSFO", change.Station);
        Assert.Contains("28R", change.CurrentFlow.Runways);
        Assert.Contains("10L", change.NewFlow.Runways);
        Assert.Contains("ILS RWY 28R", change.CurrentConditions);
        Assert.Contains("ILS RWY 10L", change.NewConditions);
    }

    [Fact]
    public async Task Declined_change_is_not_written_and_not_asked_again_for_the_same_text()
    {
        var approver = new FakeApprover(_ => ApprovalDecision.Disregard);
        var r = ApprovalRig(approver);
        await StartedWith(r, "ILS RWY 28R IN USE.");

        r.Datis.Entries = [Conditions("KSFO", "ILS RWY 10L IN USE.")];
        await WaitFor(() => approver.Requests.Count == 1, "the question");
        await Task.Delay(300); // several more refresh cycles at 40 ms
        Assert.Single(approver.Requests);
        Assert.Contains("RWY 28R", r.Vatis.StateOf("KSFO").Conditions); // vATIS still has the old flow

        // The real-world ATIS moves again: that is new text, so the controller is asked again.
        r.Datis.Entries = [Conditions("KSFO", "ILS RWY 1 IN USE.")];
        await WaitFor(() => approver.Requests.Count == 2, "a second question for new text");
        await r.Engine.StopAsync();
    }

    [Fact]
    public async Task Disregard_for_session_never_applies_a_runway_change_and_says_so_in_the_log()
    {
        var approver = new FakeApprover(_ => ApprovalDecision.DisregardForSession);
        var r = ApprovalRig(approver);
        await StartedWith(r, "ILS RWY 28R IN USE.");

        r.Datis.Entries = [Conditions("KSFO", "ILS RWY 10L IN USE.")];
        await WaitFor(() => approver.Requests.Count == 1, "the question");
        r.Datis.Entries = [Conditions("KSFO", "ILS RWY 1 IN USE.")]; // new text, but the station is disregarded
        await WaitFor(() => r.Logs.Count(l => l.Contains("change not applied")) == 1, "the log line");
        await Task.Delay(300); // several more refresh cycles at 40 ms
        var desynced = Desynced(r, "KSFO"); // stopping the sync clears it, so read it first
        await r.Engine.StopAsync();

        Assert.Single(approver.Requests);
        Assert.Single(r.Logs, l => l.Contains("change not applied")); // once per new text, not once per refresh
        Assert.Contains("RWY 28R", r.Vatis.StateOf("KSFO").Conditions); // vATIS still has the old flow
        Assert.True(desynced);
    }

    [Fact]
    public async Task Disregard_for_session_still_applies_changes_that_would_not_have_asked()
    {
        var approver = new FakeApprover(_ => ApprovalDecision.DisregardForSession);
        var r = ApprovalRig(approver);
        await StartedWith(r, "ILS RWY 28R IN USE. TWY B CLSD.");

        r.Datis.Entries = [Conditions("KSFO", "ILS RWY 10L IN USE. TWY B CLSD.")];
        await WaitFor(() => approver.Requests.Count == 1, "the question");

        // The real world goes back to the flow vATIS has, with a different taxiway closure: no question would be asked.
        r.Datis.Entries = [Conditions("KSFO", "ILS RWY 28R IN USE. TWY D CLSD.")];
        await WaitForText(r, "TWY D CLSD");
        await WaitFor(() => !Desynced(r, "KSFO"), "in step again"); // the real world is back to vATIS's runways
        await r.Engine.StopAsync();

        Assert.Single(approver.Requests);
    }

    [Fact]
    public async Task An_airport_with_two_stations_is_asked_once_and_Apply_writes_both()
    {
        var approver = new FakeApprover();
        var r = ApprovalRig(approver);
        await StartedWithBoth(r, "ILS RWY 28R IN USE.", "DEPG RWY 28L.");

        BothNow(r, "ILS RWY 10L IN USE.", "DEPG RWY 10R.");
        await WaitForBoth(r, "RWY 10");
        await r.Engine.StopAsync();

        var request = Assert.Single(approver.Requests);
        Assert.Equal("KSFO", request.Airport);
        Assert.Equal(["KSFO_A", "KSFO_D"], request.Changes.Select(c => c.Station).Order());
    }

    [Fact]
    public async Task Disregard_holds_every_station_of_the_airport_and_marks_them_all_desynced()
    {
        var approver = new FakeApprover(_ => ApprovalDecision.Disregard);
        var r = ApprovalRig(approver);
        await StartedWithBoth(r, "ILS RWY 28R IN USE.", "DEPG RWY 28L.");

        BothNow(r, "ILS RWY 10L IN USE.", "DEPG RWY 10R.");
        await WaitFor(() => approver.Requests.Count == 1, "the question");
        await WaitFor(() => Desynced(r, "KSFO_A") && Desynced(r, "KSFO_D"), "both stations desynced");
        await Task.Delay(300); // several more refresh cycles at 40 ms
        await r.Engine.StopAsync();

        Assert.Single(approver.Requests);
        Assert.Contains("RWY 28R", r.Vatis.StateOf("KSFO_A").Conditions);
        Assert.Contains("RWY 28L", r.Vatis.StateOf("KSFO_D").Conditions);
    }

    [Fact]
    public async Task A_harmless_change_on_one_station_is_shown_and_held_with_the_airport_when_the_other_needs_approval()
    {
        var approver = new FakeApprover(_ => ApprovalDecision.Disregard);
        var r = ApprovalRig(approver);
        await StartedWithBoth(r, "ILS RWY 28R IN USE.", "DEPG RWY 28L. TWY B CLSD.");

        // The arrival's runways change; the departure only gets a different taxiway closure.
        BothNow(r, "ILS RWY 10L IN USE.", "DEPG RWY 28L. TWY D CLSD.");
        await WaitFor(() => approver.Requests.Count == 1, "the question");
        await Task.Delay(300);
        var desynced = Desynced(r, "KSFO_D");
        await r.Engine.StopAsync();

        var request = Assert.Single(approver.Requests);
        Assert.Equal(["KSFO_A", "KSFO_D"], request.Changes.Select(c => c.Station)); // both are shown, since both are held
        Assert.True(request.Changes[0].FlowChanged);
        Assert.False(request.Changes[1].FlowChanged);
        Assert.Contains("TWY B CLSD", r.Vatis.StateOf("KSFO_D").Conditions); // and the airport is held as a whole
        Assert.True(desynced);
    }

    [Fact]
    public async Task A_station_that_is_not_connected_is_held_with_its_airport()
    {
        var approver = new FakeApprover(_ => ApprovalDecision.Disregard);
        var r = ApprovalRig(approver);
        await StartedWithBoth(r, "ILS RWY 28R IN USE.", "DEPG RWY 28L.");
        r.Vatis.Edit("KSFO_D", s => s.Status = "Disconnected");

        BothNow(r, "ILS RWY 10L IN USE.", "DEPG RWY 10R.");
        await WaitFor(() => approver.Requests.Count == 1, "the question");
        await Task.Delay(300);
        await r.Engine.StopAsync();

        Assert.Equal(["KSFO_A", "KSFO_D"], Assert.Single(approver.Requests).Changes.Select(c => c.Station));
        Assert.Contains("RWY 28L", r.Vatis.StateOf("KSFO_D").Conditions); // not connected, but not written either
    }

    [Fact]
    public async Task Disregard_for_session_covers_the_other_station_of_the_airport_too()
    {
        var approver = new FakeApprover(_ => ApprovalDecision.DisregardForSession);
        var r = ApprovalRig(approver);
        await StartedWithBoth(r, "ILS RWY 28R IN USE.", "DEPG RWY 28L.");

        BothNow(r, "ILS RWY 10L IN USE.", "DEPG RWY 10R.");
        await WaitFor(() => approver.Requests.Count == 1, "the question");
        BothNow(r, "ILS RWY 1 IN USE.", "DEPG RWY 1R."); // new text for both, still disregarded
        await Task.Delay(300);
        await r.Engine.StopAsync();

        Assert.Single(approver.Requests); // neither station asked again
        Assert.Contains("RWY 28R", r.Vatis.StateOf("KSFO_A").Conditions);
        Assert.Contains("RWY 28L", r.Vatis.StateOf("KSFO_D").Conditions);
    }

    [Fact]
    public async Task Resync_takes_back_a_disregard_and_asks_again_about_the_same_change()
    {
        var approver = new FakeApprover(_ => ApprovalDecision.Disregard);
        var r = ApprovalRig(approver);
        await StartedWith(r, "ILS RWY 28R IN USE.");

        r.Datis.Entries = [Conditions("KSFO", "ILS RWY 10L IN USE.")];
        await WaitFor(() => approver.Requests.Count == 1, "the first question");
        await WaitFor(() => Desynced(r, "KSFO"), "desynced");

        approver.Decide = _ => ApprovalDecision.Apply;
        r.Engine.Resync("ksfo");
        await WaitFor(() => approver.Requests.Count == 2, "the same change asked again");
        await WaitForText(r, "RWY 10L");
        await WaitFor(() => !Desynced(r, "KSFO"), "in step again");
        await r.Engine.StopAsync();

        Assert.Contains(r.Logs, l => l.Contains("resync requested"));
    }

    [Fact]
    public async Task Resync_un_mutes_an_airport_disregarded_for_the_session()
    {
        var approver = new FakeApprover(_ => ApprovalDecision.DisregardForSession);
        var r = ApprovalRig(approver);
        await StartedWith(r, "ILS RWY 28R IN USE.");

        r.Datis.Entries = [Conditions("KSFO", "ILS RWY 10L IN USE.")];
        await WaitFor(() => approver.Requests.Count == 1, "the question");
        r.Datis.Entries = [Conditions("KSFO", "ILS RWY 1 IN USE.")];
        await Task.Delay(300);
        Assert.Single(approver.Requests); // muted: not asked about the new text

        r.Engine.Resync("KSFO");
        await WaitFor(() => approver.Requests.Count == 2, "asked again after Resync");
        await r.Engine.StopAsync();
    }

    [Fact]
    public async Task Resync_does_not_wait_for_the_next_refresh()
    {
        var approver = new FakeApprover();
        var r = new Rig(Rig.Fast() with { Approval = ApprovalMode.RunwayChanges, Interval = TimeSpan.FromSeconds(30) }, approver: approver);
        await StartedWith(r, "ILS RWY 28R IN USE.");

        r.Datis.Entries = [Conditions("KSFO", "ILS RWY 10L IN USE.")]; // the next scheduled refresh is 30 s away
        r.Engine.Resync("KSFO");
        await WaitFor(() => approver.Requests.Count == 1, "the question, well before the 30 s refresh", timeoutMs: 3000);
        await r.Engine.StopAsync();
    }

    // ---- a change waiting for review does not stop the sync ----

    private static async Task StartedWithTwo(Rig r, string ksfo, string koak)
    {
        r.Vatis.Add("KSFO");
        r.Vatis.Add("KOAK");
        r.Datis.Entries = [Conditions("KSFO", ksfo), Conditions("KOAK", koak)];
        r.Engine.Start();
        await WaitIdle(r);
    }

    private static bool Awaiting(Rig r, string key) => r.Engine.Status.Stations.Single(s => s.Key == key).AwaitingReview;

    [Fact]
    public async Task An_airport_waiting_for_review_does_not_hold_up_the_others()
    {
        var approver = new FakeApprover { Block = new TaskCompletionSource() };
        var r = ApprovalRig(approver);
        await StartedWithTwo(r, "ILS RWY 28R IN USE.", "ILS RWY 28R IN USE. TWY B CLSD.");

        // KSFO changes runways and waits; KOAK only has a taxiway change.
        r.Datis.Entries = [Conditions("KSFO", "ILS RWY 10L IN USE."), Conditions("KOAK", "ILS RWY 28R IN USE. TWY D CLSD.")];
        await WaitFor(() => r.Vatis.StateOf("KOAK").Conditions.Contains("TWY D CLSD"), "KOAK to be refreshed while KSFO waits");

        Assert.Single(approver.Requests);                                 // asked once, not on every refresh
        Assert.Contains("RWY 28R", r.Vatis.StateOf("KSFO").Conditions);   // nothing written for the airport waiting
        Assert.True(Awaiting(r, "KSFO"));
        Assert.False(Awaiting(r, "KOAK"));
        await r.Engine.StopAsync();
    }

    [Fact]
    public async Task A_second_airport_with_a_runway_change_waits_beside_the_first()
    {
        var approver = new FakeApprover { Block = new TaskCompletionSource() };
        var r = ApprovalRig(approver);
        await StartedWithTwo(r, "ILS RWY 28R IN USE.", "ILS RWY 28R IN USE.");

        r.Datis.Entries = [Conditions("KSFO", "ILS RWY 10L IN USE."), Conditions("KOAK", "ILS RWY 10R IN USE.")];
        await WaitFor(() => approver.Requests.Count == 2, "both questions");
        await Task.Delay(200);

        Assert.Equal(["KOAK", "KSFO"], approver.Requests.Select(q => q.Airport).Order());
        Assert.Equal(2, approver.Requests.Count); // still two, however many refreshes passed
        Assert.True(Awaiting(r, "KSFO") && Awaiting(r, "KOAK"));
        await r.Engine.StopAsync();
    }

    [Fact]
    public async Task An_answer_given_later_is_applied_at_once_not_at_the_next_refresh()
    {
        var approver = new FakeApprover { Block = new TaskCompletionSource() };
        var r = new Rig(Rig.Fast() with { Approval = ApprovalMode.RunwayChanges, Interval = TimeSpan.FromSeconds(30) }, approver: approver);
        await StartedWith(r, "ILS RWY 28R IN USE.");

        r.Datis.Entries = [Conditions("KSFO", "ILS RWY 10L IN USE.")];
        r.Engine.Resync("KSFO"); // the next scheduled refresh is 30 s away
        await WaitFor(() => approver.Requests.Count == 1, "the question", timeoutMs: 3000);
        Assert.Contains("RWY 28R", r.Vatis.StateOf("KSFO").Conditions);

        approver.Block.SetResult(); // the controller answers Apply, a while later
        await WaitFor(() => r.Vatis.StateOf("KSFO").Conditions.Contains("RWY 10L"), "Apply to take effect well before the 30 s refresh", timeoutMs: 3000);
        Assert.False(Awaiting(r, "KSFO"));
        await r.Engine.StopAsync();
    }

    [Fact]
    public async Task A_question_is_withdrawn_and_asked_again_when_the_real_world_text_moves_on()
    {
        var approver = new FakeApprover { Block = new TaskCompletionSource() };
        var r = ApprovalRig(approver);
        await StartedWith(r, "ILS RWY 28R IN USE.");

        r.Datis.Entries = [Conditions("KSFO", "ILS RWY 10L IN USE.")];
        await WaitFor(() => approver.Requests.Count == 1, "the first question");

        // The real world moves again while the controller has not answered. What they were shown is out of date.
        r.Datis.Entries = [Conditions("KSFO", "ILS RWY 1 IN USE.")];
        await WaitFor(() => approver.Requests.Count == 2, "a new question for the new text");
        Assert.Contains("RWY 1 ", approver.Requests[1].Changes[0].NewConditions);
        Assert.Contains("RWY 28R", r.Vatis.StateOf("KSFO").Conditions);

        // Only the newest question is still open: answering the first one (already withdrawn) changes nothing.
        approver.Block.SetResult();
        await WaitFor(() => r.Vatis.StateOf("KSFO").Conditions.Contains("RWY 1 "), "the newest text to be applied");
        Assert.DoesNotContain("RWY 10L", r.Vatis.StateOf("KSFO").Conditions);
        await r.Engine.StopAsync();
    }

    [Fact]
    public async Task A_question_is_withdrawn_when_the_real_world_agrees_with_vatis_again()
    {
        var approver = new FakeApprover { Block = new TaskCompletionSource() };
        var r = ApprovalRig(approver);
        await StartedWith(r, "ILS RWY 28R IN USE.");

        r.Datis.Entries = [Conditions("KSFO", "ILS RWY 10L IN USE.")];
        await WaitFor(() => Awaiting(r, "KSFO"), "the airport to wait for review");

        r.Datis.Entries = [Conditions("KSFO", "ILS RWY 28R IN USE.")]; // back to what vATIS already has
        await WaitFor(() => !Awaiting(r, "KSFO"), "the waiting to end by itself");
        Assert.Single(approver.Requests);
        await r.Engine.StopAsync();
    }

    [Fact]
    public async Task Stopping_the_sync_withdraws_every_question()
    {
        var approver = new FakeApprover { Block = new TaskCompletionSource() };
        var r = ApprovalRig(approver);
        await StartedWith(r, "ILS RWY 28R IN USE.");
        r.Datis.Entries = [Conditions("KSFO", "ILS RWY 10L IN USE.")];
        await WaitFor(() => Awaiting(r, "KSFO"), "the airport to wait for review");

        await r.Engine.StopAsync();
        Assert.All(r.Engine.Status.Stations, s => Assert.False(s.AwaitingReview));
    }

    [Fact]
    public async Task Stopping_the_sync_clears_the_desynced_status()
    {
        var approver = new FakeApprover(_ => ApprovalDecision.Disregard);
        var r = ApprovalRig(approver);
        await StartedWith(r, "ILS RWY 28R IN USE.");

        r.Datis.Entries = [Conditions("KSFO", "ILS RWY 10L IN USE.")];
        await WaitFor(() => Desynced(r, "KSFO"), "desynced");
        await r.Engine.StopAsync();

        Assert.False(Desynced(r, "KSFO"));
    }

    [Fact]
    public async Task The_diagnostics_say_why_a_change_was_asked_about_held_or_applied()
    {
        var approver = new FakeApprover(_ => ApprovalDecision.Disregard);
        var r = ApprovalRig(approver);
        var diag = new List<string>();
        r.Engine.Diagnostics = m => { lock (diag) diag.Add(m); };
        await StartedWith(r, "ILS RWY 28R IN USE. TWY B CLSD.");

        // A taxiway change: applied without asking, and the trace says it was not a runway change.
        r.Datis.Entries = [Conditions("KSFO", "ILS RWY 28R IN USE. TWY D CLSD.")];
        await WaitForText(r, "TWY D CLSD");

        // A runway change: asked, disregarded, then not asked again.
        r.Datis.Entries = [Conditions("KSFO", "ILS RWY 10L IN USE. TWY D CLSD.")];
        await WaitFor(() => approver.Requests.Count == 1, "the question");
        await Task.Delay(300);
        await r.Engine.StopAsync();

        List<string> lines; lock (diag) lines = [.. diag];
        Assert.Contains(lines, l => l.StartsWith("sync session starting") && l.Contains("approval RunwayChanges"));
        Assert.Contains(lines, l => l.Contains("real-world D-ATIS fetch succeeded"));
        Assert.Contains(lines, l => l.StartsWith("KSFO: connected") && l.Contains("conditions differ"));
        Assert.Contains(lines, l => l.Contains("no runway change on a connected station") && l.Contains("writing without asking"));
        Assert.Contains(lines, l => l.Contains("runway change, asking the controller"));
        Assert.Contains(lines, l => l.Contains("controller chose Disregard"));
        Assert.Contains(lines, l => l.Contains("already disregarded"));
        Assert.Contains(lines, l => l.StartsWith("KSFO: held, nothing written"));
        Assert.Contains(lines, l => l.StartsWith("KSFO: written"));
        Assert.Contains(lines, l => l.StartsWith("status: Stopped"));
    }

    [Fact]
    public async Task Never_mode_applies_runway_changes_without_asking()
    {
        var approver = new FakeApprover();
        var r = ApprovalRig(approver, ApprovalMode.Never);
        await StartedWith(r, "ILS RWY 28R IN USE.");

        r.Datis.Entries = [Conditions("KSFO", "ILS RWY 10L IN USE.")];
        await WaitForText(r, "RWY 10L");
        await r.Engine.StopAsync();

        Assert.Empty(approver.Requests);
    }

    [Fact]
    public async Task Startup_pass_never_asks_even_if_vatis_already_shows_a_different_flow()
    {
        var approver = new FakeApprover();
        var r = ApprovalRig(approver);
        r.Vatis.Add("KSFO");
        r.Vatis.Edit("KSFO", s => s.Conditions = "ILS RWY 10L IN USE.");
        r.Datis.Entries = [Conditions("KSFO", "ILS RWY 28R IN USE.")];

        r.Engine.Start();
        await WaitIdle(r);
        await r.Engine.StopAsync();

        Assert.Empty(approver.Requests);
        Assert.Contains("RWY 28R", r.Vatis.StateOf("KSFO").Conditions);
    }

    [Fact]
    public async Task Stopping_while_waiting_for_an_answer_does_not_hang()
    {
        var approver = new FakeApprover { Block = new TaskCompletionSource() };
        var r = ApprovalRig(approver);
        await StartedWith(r, "ILS RWY 28R IN USE.");

        r.Datis.Entries = [Conditions("KSFO", "ILS RWY 10L IN USE.")];
        await WaitFor(() => approver.Requests.Count == 1, "the question");
        await r.Engine.StopAsync().WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Contains("RWY 28R", r.Vatis.StateOf("KSFO").Conditions); // never applied
    }

    // ---- other engine tests ----

    [Fact]
    public async Task Initial_pass_configures_connects_then_configures_again_with_letter_sync()
    {
        var r = new Rig();
        r.Vatis.Add("KSFO");
        r.Datis.Entries = [Entry("KSFO")];

        r.Engine.Start();
        await WaitIdle(r);
        await r.Engine.StopAsync();

        var calls = r.Vatis.Configures("KSFO");
        Assert.Equal(2, calls.Count);
        Assert.False(calls[0].Sync); // not connected yet: vATIS would ignore the letter
        Assert.True(calls[1].Sync);  // connected now
        Assert.Equal(["KSFO"], r.Vatis.Connects());
        Assert.Equal("ILS RWY 28R IN USE. TWY B CLSD.", calls[1].Conditions.Trim());
        Assert.Equal("TWY C CLSD.", calls[1].Notams.Trim());
    }

    private static DatisEntry FssEntry() => new("KSFO", "combined",
        "SFO ATIS INFO A 1156Z. 28010KT 10SM CLR. ILS RWY 28R IN USE. HAZD WX INFO FOR SFO AREA AVBL FM FSS. TWY B CLSD. ...ADVS YOU HAVE INFO A.",
        Now.AddMinutes(-10).ToString("yyyy-MM-ddTHH:mm:ss.ffffff"));

    [Fact]
    public async Task Built_in_cleanup_rules_apply_by_default()
    {
        var r = new Rig();
        r.Vatis.Add("KSFO");
        r.Datis.Entries = [FssEntry()];

        r.Engine.Start();
        await WaitIdle(r);
        await r.Engine.StopAsync();

        Assert.Equal("ILS RWY 28R IN USE. TWY B CLSD.", r.Vatis.Configures("KSFO")[^1].Conditions.Trim());
    }

    [Fact]
    public async Task A_rule_the_user_switched_off_leaves_the_text_alone()
    {
        var r = new Rig(Rig.Fast() with { DisabledRules = ["flight-service-pointers"] });
        r.Vatis.Add("KSFO");
        r.Datis.Entries = [FssEntry()];

        r.Engine.Start();
        await WaitIdle(r);
        await r.Engine.StopAsync();

        Assert.Contains("HAZD WX INFO FOR SFO AREA AVBL FM FSS.", r.Vatis.Configures("KSFO")[^1].Conditions);
    }

    private static DatisEntry FrequencyEntry() => new("KSFO", "combined",
        "SFO ATIS INFO A 1156Z. 28010KT 10SM CLR. ILS RWY 28R IN USE. CTC GND ON 121.65 FOR TAXI. TWR FREQ 120.5. TWY B CLSD. ...ADVS YOU HAVE INFO A.",
        Now.AddMinutes(-10).ToString("yyyy-MM-ddTHH:mm:ss.ffffff"));

    [Fact]
    public async Task Frequency_settings_remove_or_replace_frequencies_per_position()
    {
        var r = new Rig(Rig.Fast() with
        {
            Frequencies = new Dictionary<FrequencyRole, FrequencySetting>
            {
                [FrequencyRole.Ground] = new(FrequencyMode.Remove),
                [FrequencyRole.Tower] = new(FrequencyMode.Override, "118.5"),
            },
        });
        r.Vatis.Add("KSFO");
        r.Datis.Entries = [FrequencyEntry()];

        r.Engine.Start();
        await WaitIdle(r);
        await r.Engine.StopAsync();

        Assert.Equal("ILS RWY 28R IN USE. TWR FREQ 118.5. TWY B CLSD.", r.Vatis.Configures("KSFO")[^1].Conditions.Trim());
    }

    [Fact]
    public async Task An_airport_that_customizes_frequencies_does_not_use_the_defaults()
    {
        var r = new Rig(Rig.Fast() with
        {
            Frequencies = new Dictionary<FrequencyRole, FrequencySetting>
                { [FrequencyRole.Ground] = new(FrequencyMode.Remove) },
            PerAirport = new Dictionary<string, AirportRules>
            {
                ["KOAK"] = new([], new Dictionary<FrequencyRole, FrequencySetting>
                    { [FrequencyRole.Ground] = new(FrequencyMode.Override, "121.9") }),
            },
        });
        r.Vatis.Add("KSFO");
        r.Vatis.Add("KOAK");
        r.Datis.Entries = [FrequencyEntry(), FrequencyEntry() with { Airport = "KOAK" }];

        r.Engine.Start();
        await WaitIdle(r);
        await r.Engine.StopAsync();

        Assert.Equal("ILS RWY 28R IN USE. TWR FREQ 120.5. TWY B CLSD.", r.Vatis.Configures("KSFO")[^1].Conditions.Trim()); // default: removed
        Assert.Equal("ILS RWY 28R IN USE. CTC GND ON 121.9 FOR TAXI. TWR FREQ 120.5. TWY B CLSD.", r.Vatis.Configures("KOAK")[^1].Conditions.Trim()); // own: replaced
    }

    [Fact]
    public async Task An_airport_can_switch_off_a_built_in_rule_that_stays_on_elsewhere()
    {
        var r = new Rig(Rig.Fast() with
        {
            PerAirport = new Dictionary<string, AirportRules>
            {
                ["KOAK"] = new(["flight-service-pointers"], new Dictionary<FrequencyRole, FrequencySetting>()),
            },
        });
        r.Vatis.Add("KSFO");
        r.Vatis.Add("KOAK");
        r.Datis.Entries = [FssEntry(), FssEntry() with { Airport = "KOAK" }];

        r.Engine.Start();
        await WaitIdle(r);
        await r.Engine.StopAsync();

        Assert.DoesNotContain("FSS", r.Vatis.Configures("KSFO")[^1].Conditions);
        Assert.Contains("FSS", r.Vatis.Configures("KOAK")[^1].Conditions);
    }

    [Fact]
    public async Task Frequencies_are_left_alone_by_default()
    {
        var r = new Rig();
        r.Vatis.Add("KSFO");
        r.Datis.Entries = [FrequencyEntry()];

        r.Engine.Start();
        await WaitIdle(r);
        await r.Engine.StopAsync();

        Assert.Contains("CTC GND ON 121.65 FOR TAXI. TWR FREQ 120.5.", r.Vatis.Configures("KSFO")[^1].Conditions);
    }

    [Fact]
    public async Task Letter_is_synced_only_once_across_refreshes()
    {
        var r = new Rig(Rig.Fast() with { Interval = TimeSpan.FromMilliseconds(40) });
        r.Vatis.Add("KSFO");
        r.Datis.Entries = [Entry("KSFO")];

        r.Engine.Start();
        await WaitFor(() => r.Vatis.Configures("KSFO").Count >= 5, "two refresh cycles");
        await r.Engine.StopAsync();

        Assert.Single(r.Vatis.Configures("KSFO"), c => c.Sync);
    }

    [Fact]
    public async Task Letter_is_never_synced_after_startup_even_for_a_station_connected_later()
    {
        var r = new Rig(Rig.Fast() with { Airports = ["KSFO"], Interval = TimeSpan.FromMilliseconds(40) });
        r.Vatis.Add("KSFO");
        r.Vatis.Add("KOAK"); // not selected, so left disconnected at startup
        r.Datis.Entries = [Entry("KSFO"), Entry("KOAK")];

        r.Engine.Start();
        await WaitIdle(r);
        Assert.Single(r.Vatis.Configures("KSFO"), c => c.Sync); // startup syncs the letter once

        // The controller connects KOAK by hand mid-session; it must be refreshed but its letter left alone.
        r.Vatis.Edit("KOAK", s => s.Status = "Connected");
        var before = r.Vatis.Configures("KOAK").Count;
        await WaitFor(() => r.Vatis.Configures("KOAK").Count >= before + 2, "KOAK to be refreshed");
        await r.Engine.StopAsync();

        Assert.All(r.Vatis.Configures("KOAK"), c => Assert.False(c.Sync));
        Assert.Single(r.Vatis.Configures("KSFO"), c => c.Sync);
    }

    [Fact]
    public async Task Only_stations_with_a_datis_preset_are_touched()
    {
        var r = new Rig();
        r.Vatis.Add("KSFO");
        r.Vatis.Add("KOAK", preset: "Manual");
        r.Datis.Entries = [Entry("KSFO"), Entry("KOAK")];

        r.Engine.Start();
        await WaitIdle(r);
        await r.Engine.StopAsync();

        Assert.Empty(r.Vatis.Configures("KOAK"));
        Assert.DoesNotContain("KOAK", r.Vatis.Connects());
    }

    [Fact]
    public async Task Arrival_and_departure_stations_get_their_own_text()
    {
        var r = new Rig();
        r.Vatis.Add("KATL", "Arrival");
        r.Vatis.Add("KATL", "Departure");
        r.Datis.Entries =
        [
            new("KATL", "arr", "H1. H2. ARRIVAL TEXT. ...ADVS YOU HAVE INFO I.", Entry("KATL").UpdatedAt),
            new("KATL", "dep", "H1. H2. DEPARTURE TEXT. ...ADVS YOU HAVE INFO V.", Entry("KATL").UpdatedAt),
        ];

        r.Engine.Start();
        await WaitIdle(r);
        await r.Engine.StopAsync();

        Assert.Equal("ARRIVAL TEXT.", r.Vatis.StateOf("KATL_A").Conditions.Trim());
        Assert.Equal("DEPARTURE TEXT.", r.Vatis.StateOf("KATL_D").Conditions.Trim());
    }

    [Fact]
    public async Task Never_connects_more_than_the_limit()
    {
        var r = new Rig(Rig.Fast() with { MaxConnections = 2 });
        foreach (var a in new[] { "KSFO", "KOAK", "KSJC" }) { r.Vatis.Add(a); }
        r.Datis.Entries = [Entry("KSFO"), Entry("KOAK"), Entry("KSJC")];

        r.Engine.Start();
        await WaitIdle(r);
        await r.Engine.StopAsync();

        Assert.Equal(["KSFO", "KOAK"], r.Vatis.Connects());
    }

    [Fact]
    public async Task Already_connected_stations_count_toward_the_limit()
    {
        var r = new Rig(Rig.Fast() with { MaxConnections = 2 });
        r.Vatis.Add("KSFO", status: "Connected");
        r.Vatis.Add("KOAK");
        r.Vatis.Add("KSJC");
        r.Datis.Entries = [Entry("KSFO"), Entry("KOAK"), Entry("KSJC")];

        r.Engine.Start();
        await WaitIdle(r);
        await r.Engine.StopAsync();

        Assert.Equal(["KOAK"], r.Vatis.Connects());
    }

    [Fact]
    public async Task Connections_over_the_limit_are_trimmed()
    {
        var r = new Rig(Rig.Fast() with { MaxConnections = 2 });
        foreach (var a in new[] { "KSFO", "KOAK", "KSJC" }) r.Vatis.Add(a, status: "Connected");
        r.Datis.Entries = [Entry("KSFO"), Entry("KOAK"), Entry("KSJC")];

        r.Engine.Start();
        await WaitIdle(r);
        await r.Engine.StopAsync();

        Assert.Equal(["KSJC"], r.Vatis.Disconnects());
    }

    [Fact]
    public async Task Airport_selection_limits_what_is_connected_but_every_profile_station_is_refreshed()
    {
        var r = new Rig(Rig.Fast() with { Airports = ["ksfo"] });
        r.Vatis.Add("KSFO");
        r.Vatis.Add("KOAK");
        r.Datis.Entries = [Entry("KSFO"), Entry("KOAK")];

        r.Engine.Start();
        await WaitFor(() => r.Vatis.Configures("KSFO").Count >= 3, "a refresh cycle");
        await r.Engine.StopAsync();

        Assert.Equal(["KSFO"], r.Vatis.Connects());
        // KOAK is not connected, but it is in the profile, so it is refreshed like the rest.
        Assert.True(r.Vatis.Configures("KOAK").Count >= 3);
    }

    [Fact]
    public async Task Active_tower_position_connects_only_that_airport()
    {
        var r = new Rig(Rig.Fast() with { Airports = [] }, new CrcPosition("OAK", "TWR"));
        r.Vatis.Add("KSFO");
        r.Vatis.Add("KOAK");
        r.Datis.Entries = [Entry("KSFO"), Entry("KOAK")];

        r.Engine.Start();
        await WaitIdle(r);
        await r.Engine.StopAsync();

        Assert.Equal(["KOAK"], r.Vatis.Connects());
    }

    [Fact]
    public async Task With_no_selection_and_no_position_every_datis_station_in_the_profile_is_connected()
    {
        var r = new Rig(Rig.Fast() with { Airports = [] });
        r.Vatis.Add("KSFO");
        r.Vatis.Add("KOAK");
        r.Vatis.Add("KSJC", preset: "Other"); // in the profile but without a D-ATIS preset
        r.Datis.Entries = [Entry("KSFO"), Entry("KOAK")];

        r.Engine.Start();
        await WaitIdle(r);
        await r.Engine.StopAsync();

        Assert.Equal(["KOAK", "KSFO"], r.Vatis.Connects().Order());
    }

    [Fact]
    public async Task Profile_connecting_respects_the_connection_limit_and_what_is_already_connected()
    {
        var r = new Rig(Rig.Fast() with { Airports = [], MaxConnections = 2 });
        r.Vatis.Add("KSFO", status: "Connected"); // already up, counts toward the limit
        r.Vatis.Add("KOAK");
        r.Vatis.Add("KSJC");
        r.Datis.Entries = [Entry("KSFO"), Entry("KOAK"), Entry("KSJC")];

        r.Engine.Start();
        await WaitIdle(r);
        await r.Engine.StopAsync();

        Assert.Equal(["KOAK"], r.Vatis.Connects());
        Assert.Empty(r.Vatis.Disconnects());
    }

    [Fact]
    public async Task A_followed_position_still_beats_connecting_the_whole_profile()
    {
        var r = new Rig(Rig.Fast() with { Airports = [] }, new CrcPosition("OAK", "GND"));
        r.Vatis.Add("KSFO");
        r.Vatis.Add("KOAK");
        r.Datis.Entries = [Entry("KSFO"), Entry("KOAK")];

        r.Engine.Start();
        await WaitIdle(r);
        await r.Engine.StopAsync();

        Assert.Equal(["KOAK"], r.Vatis.Connects());
    }

    [Fact]
    public async Task Explicit_selection_wins_over_the_active_position()
    {
        var r = new Rig(Rig.Fast() with { Airports = ["KSFO"] }, new CrcPosition("OAK", "TWR"));
        r.Vatis.Add("KSFO");
        r.Vatis.Add("KOAK");
        r.Datis.Entries = [Entry("KSFO"), Entry("KOAK")];

        r.Engine.Start();
        await WaitIdle(r);
        await r.Engine.StopAsync();

        Assert.Equal(["KSFO"], r.Vatis.Connects());
    }

    [Fact]
    public async Task Non_tower_position_with_no_selection_connects_nothing_when_profile_connecting_is_off()
    {
        var r = new Rig(Rig.Fast() with { Airports = [], ConnectProfileStations = false }, new CrcPosition("OAK", "APP"));
        r.Vatis.Add("KSFO");
        r.Vatis.Add("KOAK");
        r.Datis.Entries = [Entry("KSFO"), Entry("KOAK")];

        r.Engine.Start();
        await WaitIdle(r);
        await r.Engine.StopAsync();

        Assert.Empty(r.Vatis.Connects());
    }

    [Fact]
    public async Task Position_following_can_be_turned_off()
    {
        var r = new Rig(Rig.Fast() with { Airports = [], AutoConnectActivePosition = false, ConnectProfileStations = false },
            new CrcPosition("OAK", "TWR"));
        r.Vatis.Add("KSFO");
        r.Vatis.Add("KOAK");
        r.Datis.Entries = [Entry("KSFO"), Entry("KOAK")];

        r.Engine.Start();
        await WaitIdle(r);
        await r.Engine.StopAsync();

        Assert.Empty(r.Vatis.Connects());
    }

    [Fact]
    public async Task With_nothing_to_connect_every_profile_station_is_still_refreshed()
    {
        var r = new Rig(Rig.Fast() with
            { Airports = [], ConnectProfileStations = false, Interval = TimeSpan.FromMilliseconds(60) });
        r.Vatis.Add("KSFO", status: "Connected"); // user connected it by hand in vATIS
        r.Vatis.Add("KOAK");
        r.Datis.Entries = [Entry("KSFO"), Entry("KOAK")];

        r.Engine.Start();
        await WaitFor(() => r.Vatis.Configures("KSFO").Count >= 3, "a refresh cycle");
        await r.Engine.StopAsync();

        Assert.Empty(r.Vatis.Connects());
        Assert.True(r.Vatis.Configures("KOAK").Count >= 3); // never connected, still kept up to date
    }

    [Fact]
    public async Task Station_without_datis_is_marked_unavailable_but_still_configured_initially()
    {
        var r = new Rig();
        r.Vatis.Add("KSFO");
        r.Datis.Entries = []; // nothing from atis.info

        r.Engine.Start();
        await WaitIdle(r);
        await r.Engine.StopAsync();

        Assert.All(r.Vatis.Configures("KSFO"), c => Assert.Equal("D-ATIS NOT AVBL.", c.Conditions));
        Assert.All(r.Vatis.Configures("KSFO"), c => Assert.False(c.Sync)); // never sync a letter without data
        Assert.False(r.Engine.Status.Stations.Single().DatisAvailable);
    }

    [Fact]
    public async Task Refresh_does_not_overwrite_text_with_NOT_AVBL()
    {
        var r = new Rig(Rig.Fast() with { Interval = TimeSpan.FromMilliseconds(150) });
        r.Vatis.Add("KSFO");
        r.Datis.Entries = [Entry("KSFO")];

        r.Engine.Start();
        await WaitIdle(r);
        r.Datis.Entries = []; // atis.info lost the airport
        await Task.Delay(500);   // several refresh cycles
        await r.Engine.StopAsync();

        Assert.DoesNotContain("NOT AVBL", r.Vatis.StateOf("KSFO").Conditions);
        Assert.Equal(2, r.Vatis.Configures("KSFO").Count);
    }

    [Fact]
    public async Task Text_the_user_deleted_by_hand_stays_deleted()
    {
        var r = new Rig(Rig.Fast() with { Interval = TimeSpan.FromMilliseconds(300) });
        r.Vatis.Add("KSFO");
        r.Datis.Entries = [Entry("KSFO")];

        r.Engine.Start();
        await WaitIdle(r); // initial pass and its snapshot are done; the first refresh is ~300 ms away
        r.Vatis.Edit("KSFO", s => s.Conditions = s.Conditions.Replace("TWY B CLSD. ", ""));
        await WaitFor(() => r.Vatis.Configures("KSFO").Count >= 3, "first refresh");
        await r.Engine.StopAsync();

        var refreshed = r.Vatis.Configures("KSFO")[2];
        Assert.Contains("ILS RWY 28R IN USE.", refreshed.Conditions);
        Assert.DoesNotContain("TWY B CLSD", refreshed.Conditions);
        Assert.Contains("TWY C CLSD", refreshed.Notams); // untouched text still refreshes
    }

    [Fact]
    public async Task Deletions_involving_contraction_variables_are_matched_against_raw_text()
    {
        var r = new Rig(Rig.Fast() with { Interval = TimeSpan.FromMilliseconds(300) });
        r.Vatis.Add("KSFO");
        r.Vatis.Contractions["KSFO"] = [new("TB", "TWY B CLSD")];
        r.Datis.Entries = [Entry("KSFO")];

        r.Engine.Start();
        await WaitIdle(r);
        Assert.Contains("@TB", r.Vatis.StateOf("KSFO").Conditions); // the text was written with the variable
        r.Vatis.Edit("KSFO", s => s.Conditions = s.Conditions.Replace("@TB. ", "")); // user deletes the whole "@TB." word
        await WaitFor(() => r.Vatis.Configures("KSFO").Count >= 3, "first refresh");
        await r.Engine.StopAsync();

        Assert.DoesNotContain("@TB", r.Vatis.Configures("KSFO")[2].Conditions);
        Assert.DoesNotContain("TWY B", r.Vatis.Configures("KSFO")[2].Conditions);
    }

    [Fact]
    public async Task Status_reports_stations_and_times()
    {
        var r = new Rig();
        r.Vatis.Add("KSFO");
        r.Vatis.Add("KOAK");
        r.Datis.Entries = [Entry("KSFO")];

        r.Engine.Start();
        await WaitIdle(r);
        var status = r.Engine.Status;
        await r.Engine.StopAsync();

        Assert.Equal(SyncPhase.Idle, status.Phase);
        Assert.Equal(["KOAK", "KSFO"], status.Stations.Select(s => s.Key));
        Assert.True(status.Stations.Single(s => s.Key == "KSFO").Connected);
        Assert.True(status.Stations.Single(s => s.Key == "KSFO").DatisAvailable);
        Assert.False(status.Stations.Single(s => s.Key == "KOAK").DatisAvailable);
        Assert.Equal(Now, status.LastSync);
        Assert.Equal(Now + TimeSpan.FromMilliseconds(250), status.NextSync);
    }

    [Fact]
    public async Task Waits_while_vatis_is_down_then_proceeds()
    {
        var r = new Rig();
        r.Vatis.Add("KSFO");
        r.Datis.Entries = [Entry("KSFO")];
        r.Vatis.Down = true;

        r.Engine.Start();
        await WaitFor(() => r.Engine.Status.Phase == SyncPhase.WaitingForVatis, "waiting state");
        Assert.Empty(r.Vatis.AllConfigures());

        r.Vatis.Down = false;
        await WaitIdle(r);
        await r.Engine.StopAsync();

        Assert.NotEmpty(r.Vatis.Configures("KSFO"));
    }

    [Fact]
    public async Task A_problem_is_flagged_as_an_error_and_the_flag_clears_when_syncing_resumes()
    {
        var r = new Rig();
        r.Vatis.Add("KSFO");
        r.Datis.Entries = [Entry("KSFO")];
        r.Vatis.Down = true;

        r.Engine.Start();
        await WaitFor(() => r.Engine.Status.IsError, "an error status while vATIS is down");
        Assert.Equal(SyncPhase.WaitingForVatis, r.Engine.Status.Phase);

        r.Vatis.Down = false;
        await WaitIdle(r);
        await r.Engine.StopAsync();

        Assert.False(r.Engine.Status.IsError);
    }

    [Fact]
    public async Task Redoes_the_initial_pass_after_vatis_restarts_midrun()
    {
        var r = new Rig(Rig.Fast() with { Interval = TimeSpan.FromMilliseconds(150) });
        r.Vatis.Add("KSFO");
        r.Datis.Entries = [Entry("KSFO")];

        r.Engine.Start();
        await WaitIdle(r);

        r.Vatis.Down = true;
        await WaitFor(() => r.Engine.Status.Phase == SyncPhase.WaitingForVatis, "noticing vATIS is gone");
        r.Vatis.Edit("KSFO", s => s.Status = "Disconnected"); // a restarted vATIS starts disconnected
        r.Vatis.Down = false;

        await WaitFor(() => r.Vatis.Connects().Count >= 2, "reconnecting after the restart");
        await r.Engine.StopAsync();
    }

    [Fact]
    public async Task A_hiccup_does_not_sync_the_letter_of_a_station_that_is_still_connected()
    {
        var r = new Rig(Rig.Fast() with { Interval = TimeSpan.FromMilliseconds(40) });
        r.Vatis.Add("KSFO");
        r.Datis.Entries = [Entry("KSFO")];

        r.Engine.Start();
        await WaitIdle(r);
        Assert.Single(r.Vatis.Configures("KSFO"), c => c.Sync); // the letter is set once, after Start

        // vATIS stops answering for a moment but KSFO stays connected; the engine starts over by itself.
        r.Vatis.Down = true;
        await WaitFor(() => r.Engine.Status.Phase == SyncPhase.WaitingForVatis, "noticing vATIS is gone");
        var before = r.Vatis.Configures("KSFO").Count;
        r.Vatis.Down = false;
        await WaitFor(() => r.Vatis.Configures("KSFO").Count >= before + 3, "writing again after the hiccup");
        await r.Engine.StopAsync();

        Assert.Single(r.Vatis.Configures("KSFO"), c => c.Sync); // still only the one from Start
    }

    [Fact]
    public async Task A_station_reconnected_after_vatis_restarts_gets_its_letter_again()
    {
        var r = new Rig(Rig.Fast() with { Interval = TimeSpan.FromMilliseconds(40) });
        r.Vatis.Add("KSFO");
        r.Datis.Entries = [Entry("KSFO")];

        r.Engine.Start();
        await WaitIdle(r);

        r.Vatis.Down = true;
        await WaitFor(() => r.Engine.Status.Phase == SyncPhase.WaitingForVatis, "noticing vATIS is gone");
        r.Vatis.Edit("KSFO", s => s.Status = "Disconnected"); // a restarted vATIS starts disconnected
        r.Vatis.Down = false;

        await WaitFor(() => r.Vatis.Configures("KSFO").Count(c => c.Sync) == 2, "the letter after reconnecting");
        await r.Engine.StopAsync();
    }

    [Fact]
    public async Task Launches_vatis_when_asked_and_not_when_disabled()
    {
        var on = new Rig();
        on.Vatis.Add("KSFO");
        on.Engine.Start();
        await WaitIdle(on);
        await on.Engine.StopAsync();
        Assert.Equal(1, on.Launcher.Calls);

        var off = new Rig(Rig.Fast() with { LaunchVatis = false });
        off.Vatis.Add("KSFO");
        off.Engine.Start();
        await WaitIdle(off);
        await off.Engine.StopAsync();
        Assert.Equal(0, off.Launcher.Calls);
    }

    [Fact]
    public async Task Stop_ends_the_run_and_reports_Stopped()
    {
        var r = new Rig();
        r.Vatis.Add("KSFO");
        r.Datis.Entries = [Entry("KSFO")];

        r.Engine.Start();
        await WaitIdle(r);
        Assert.True(r.Engine.IsRunning);

        await r.Engine.StopAsync();

        Assert.False(r.Engine.IsRunning);
        Assert.Equal(SyncPhase.Stopped, r.Engine.Status.Phase);
    }

    [Fact]
    public async Task Can_be_started_again_after_stopping()
    {
        var r = new Rig();
        r.Vatis.Add("KSFO");
        r.Datis.Entries = [Entry("KSFO")];

        r.Engine.Start();
        await WaitIdle(r);
        await r.Engine.StopAsync();

        r.Engine.Start();
        await WaitIdle(r);
        Assert.True(r.Engine.IsRunning);
        await r.Engine.StopAsync();
    }

    [Fact]
    public async Task Start_while_running_does_not_start_a_second_loop()
    {
        var r = new Rig();
        r.Vatis.Add("KSFO");
        r.Datis.Entries = [Entry("KSFO")];

        r.Engine.Start();
        r.Engine.Start();
        await WaitIdle(r);
        await r.Engine.StopAsync();

        Assert.Equal(1, r.Launcher.Calls);
    }

    [Fact]
    public async Task Stop_before_start_is_harmless()
    {
        var r = new Rig();
        await r.Engine.StopAsync();
        Assert.Equal(SyncPhase.Stopped, r.Engine.Status.Phase);
    }
}
