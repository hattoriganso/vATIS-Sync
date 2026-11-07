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

    private sealed class Rig
    {
        public FakeVatis Vatis { get; } = new();
        public FakeDatis Datis { get; } = new();
        public FakeLauncher Launcher { get; init; } = new();
        public SyncEngine Engine { get; }
        public List<string> Logs { get; } = [];

        public Rig(SyncOptions? options = null, CrcPosition? position = null)
        {
            options ??= Fast();
            Engine = new SyncEngine(Vatis, Datis, new NoReplacements(), new FakePosition(position),
                Launcher, options, new FixedTime(Now));
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
    public async Task Airport_selection_limits_what_is_connected_and_refreshed()
    {
        var r = new Rig(Rig.Fast() with { Airports = ["ksfo"] });
        r.Vatis.Add("KSFO");
        r.Vatis.Add("KOAK");
        r.Datis.Entries = [Entry("KSFO"), Entry("KOAK")];

        r.Engine.Start();
        await WaitFor(() => r.Vatis.Configures("KSFO").Count >= 3, "a refresh cycle");
        await r.Engine.StopAsync();

        Assert.Equal(["KSFO"], r.Vatis.Connects());
        // KOAK got the two initial-pass writes, but refreshes only touch connected stations.
        Assert.Equal(2, r.Vatis.Configures("KOAK").Count);
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
    public async Task Non_tower_position_with_no_selection_connects_nothing()
    {
        var r = new Rig(Rig.Fast() with { Airports = [] }, new CrcPosition("OAK", "APP"));
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
        var r = new Rig(Rig.Fast() with { Airports = [], AutoConnectActivePosition = false },
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
    public async Task With_nothing_to_connect_stations_the_user_already_connected_are_still_refreshed()
    {
        var r = new Rig(Rig.Fast() with { Airports = [], Interval = TimeSpan.FromMilliseconds(60) });
        r.Vatis.Add("KSFO", status: "Connected"); // user connected it by hand in vATIS
        r.Vatis.Add("KOAK");
        r.Datis.Entries = [Entry("KSFO"), Entry("KOAK")];

        r.Engine.Start();
        await WaitFor(() => r.Vatis.Configures("KSFO").Count >= 3, "a refresh cycle");
        await r.Engine.StopAsync();

        Assert.Empty(r.Vatis.Connects());
        Assert.Equal(2, r.Vatis.Configures("KOAK").Count); // initial writes only; never connected
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
