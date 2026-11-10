namespace VatisSync.Core;

/// <summary>
/// The sync loop, ported from main() / configure_atises() / connect_atises() in vATISLoad.py:
/// push real-world D-ATIS text into vATIS, connect the wanted stations, then refresh every interval.
/// Unlike the script, it survives vATIS closing or restarting (it waits, then starts over).
/// </summary>
public sealed class SyncEngine(
    IVatisClient client,
    IDatisSource datis,
    IReplacementSource replacements,
    IActivePositionProvider positions,
    IVatisLauncher launcher,
    SyncOptions options,
    TimeProvider? time = null,
    IUpdateApprover? approver = null)
{
    private static readonly string[] FollowedSuffixes = ["TWR", "GND", "DEL", "RMP"];

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly object _gate = new();
    private readonly Dictionary<string, StationStatus> _stations = [];

    // Per sync session, by airport (an airport's stations are synced together or not at all): text the controller
    // declined (so it is not asked about again) and airports they chose to stop being asked about.
    private readonly Dictionary<string, string> _declined = [];
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _resyncs = new();
    private TaskCompletionSource _wake = NewWake();
    private readonly HashSet<string> _mutedForSession = [];

    // The fill the controller asked for by pressing Start has been done. From then on nothing reaches a connected
    // ATIS unchecked, including when the engine starts over by itself because vATIS stopped answering.
    private bool _startFillDone;

    private SyncStatus _status = SyncStatus.Initial;
    private CancellationTokenSource? _cts;
    private Task? _run;

    public SyncStatus Status => _status;

    /// <summary>Told what the engine decided and why (for the activity log). Detail only: the Log panel is <see cref="LogEntry"/>.</summary>
    public Action<string>? Diagnostics { get; set; }

    private void Diag(string message) => Diagnostics?.Invoke(message);
    public bool IsRunning => _run is { IsCompleted: false };

    public event EventHandler<SyncStatus>? StatusChanged;
    public event Action<string>? Log;
    /// <summary>Same lines as <see cref="Log"/>, with how serious each one is.</summary>
    public event Action<string, LogKind>? LogEntry;

    // ---- start / stop (for the UI) ----

    public void Start()
    {
        lock (_gate)
        {
            if (IsRunning) return;
            _cts = new CancellationTokenSource();
            _run = Task.Run(() => RunAsync(_cts.Token));
        }
    }

    /// <summary>
    /// Takes back a Disregard for an airport and checks the real-world ATIS now, without waiting for the next refresh.
    /// If a change still needs approval the controller is asked again; nothing is applied without Apply.
    /// </summary>
    public void Resync(string airport)
    {
        _resyncs.Enqueue(airport.ToUpperInvariant());
        _wake.TrySetResult();
    }

    private static TaskCompletionSource NewWake() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task StopAsync()
    {
        Task? run;
        lock (_gate)
        {
            _cts?.Cancel();
            run = _run;
        }
        if (run is not null)
        {
            try { await run.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
    }

    // ---- main loop ----

    /// <summary>Runs until cancelled. Returns normally on cancellation, with phase Stopped.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        _declined.Clear();
        _mutedForSession.Clear();
        _startFillDone = false;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await RunSessionAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (VatisUnavailableException ex)
                {
                    Diag($"vATIS unavailable: {ex}");
                    Info(Messages.Get(Msg.EngineUnreachableLog, new { reason = ex.Message }), LogKind.Warning);
                    Publish(SyncPhase.WaitingForVatis, Messages.Get(Msg.EngineUnreachable), isError: true);
                }
                catch (Exception ex)
                {
                    Info(Messages.Get(Msg.EngineUnexpectedLog, new { error = ex }), LogKind.Error);
                    Publish(SyncPhase.WaitingForVatis, Messages.Get(Msg.EngineUnexpected), isError: true);
                }

                try { await Task.Delay(options.RetryDelay, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }
        finally
        {
            ClearPending();
            Publish(SyncPhase.Stopped, null);
        }
    }

    /// <summary>One continuous stretch of talking to a running vATIS. Throws if vATIS goes away.</summary>
    private async Task RunSessionAsync(CancellationToken ct)
    {
        // What the controller disregarded is kept: starting over after a hiccup is not a new Start.
        _stations.Clear();
        var letters = new LetterSync();
        Diag($"sync session starting: refresh every {options.Interval.TotalMinutes:0.##} min, approval {options.Approval}, " +
             $"max connections {options.MaxConnections}, airports [{string.Join(", ", options.Airports)}], " +
             $"follow CRC position {options.AutoConnectActivePosition}, connect profile stations {options.ConnectProfileStations}, " +
             $"launch vATIS {options.LaunchVatis}, approver {(approver is null ? "none" : "present")}");

        Publish(SyncPhase.Starting, Messages.Get(Msg.EngineStarting));
        if (options.LaunchVatis)
        {
            var launched = launcher.EnsureRunning();
            if (launched == LaunchResult.NotFound)
                Info(Messages.Get(Msg.EngineVatisNotFound), LogKind.Error);
        }

        Publish(SyncPhase.WaitingForVatis, Messages.Get(Msg.EngineWaiting));
        if (!await client.WaitUntilReadyAsync(options.StartupTimeout, ct).ConfigureAwait(false))
            throw new VatisUnavailableException("vATIS did not respond.");

        // Initial pass. The first configure runs while stations are still disconnected, so vATIS
        // can't apply the ATIS letter yet; the second one (after connecting) sets it.
        // Only the fill that follows Start is written unchecked. When the engine starts over by itself (vATIS missed
        // a reply, or was restarted), a station that is still connected is live, so it is checked like any refresh.
        var ask = _startFillDone;
        if (ask) Diag("starting over without a new Start: connected stations are checked before anything is written");
        Publish(SyncPhase.Syncing, Messages.Get(Msg.EngineLoading));
        await ConfigureAllAsync(connectedOnly: false, ask, removals: null, letters, ct).ConfigureAwait(false);
        _startFillDone = true;
        await ConnectAsync(letters, ct).ConfigureAwait(false);
        await ConfigureAllAsync(connectedOnly: false, ask, removals: null, letters, ct).ConfigureAwait(false);
        letters.EndStartup(); // from here on vATIS advances its own letter; we only send text

        var snapshot = await SnapshotAsync(ct).ConfigureAwait(false);
        if (options.PostConnectDelay > TimeSpan.Zero)
            await Task.Delay(options.PostConnectDelay, ct).ConfigureAwait(false);
        await DisconnectOverLimitAsync(ct).ConfigureAwait(false);

        var lastSync = _time.GetUtcNow();
        Dictionary<string, List<string>>? removals = null;

        while (true)
        {
            Publish(SyncPhase.Idle, null, lastSync, lastSync + options.Interval);

            // Wait for the next refresh, or for a Resync request, which does not wait. The wake-up is created before
            // the queue is checked so a request made at any moment is seen by one or the other.
            var wake = _wake = NewWake();
            if (_resyncs.IsEmpty)
            {
                await Task.WhenAny(Task.Delay(options.Interval, ct), wake.Task).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
            }
            while (_resyncs.TryDequeue(out var airport))
            {
                _mutedForSession.Remove(airport);
                _declined.Remove(airport);
                Info(Messages.Get(Msg.EngineResync, new { station = airport }));
            }
            Publish(SyncPhase.Syncing, Messages.Get(Msg.EngineRefreshing), lastSync);

            // After the first interval, treat text the user deleted by hand as "keep deleted".
            if (removals is null)
                removals = await BuildRemovalsAsync(snapshot, ct).ConfigureAwait(false);

            await ConfigureAllAsync(connectedOnly: true, ask: true, removals, letters, ct).ConfigureAwait(false);
            lastSync = _time.GetUtcNow();
        }
    }

    // ---- configure ----

    private async Task ConfigureAllAsync(
        bool connectedOnly,
        bool ask,
        Dictionary<string, List<string>>? removals,
        LetterSync letters,
        CancellationToken ct)
    {
        var stations = await DatisStationsAsync(ct).ConfigureAwait(false);
        var airports = stations.Select(s => s.Key[..4]).Distinct();
        var rules = await replacements.GetAsync(airports, ct).ConfigureAwait(false);
        var builtInByAirport = new Dictionary<string, ReplacementRules>();

        var fetch = await datis.FetchAsync(ct).ConfigureAwait(false);
        Diag($"{(connectedOnly ? "refresh" : "initial fill")}: real-world D-ATIS fetch " +
             (fetch.Succeeded ? "succeeded" : $"FAILED ({fetch.Error})") +
             $"{(fetch.FromCache ? ", using the saved copy" : "")}: {fetch.Entries.Count} entries; {stations.Count} vATIS stations [{string.Join(", ", stations.Select(s => s.Key))}]");
        if (!fetch.Succeeded)
            Info(Messages.Get(fetch.FromCache ? Msg.EngineDatisFailedCached : Msg.EngineDatisFailedNone, new { reason = fetch.Error }), LogKind.Warning);
        var now = _time.GetUtcNow();

        // First work out what every station would get, then decide per airport, then write: an airport's
        // stations are written together or not at all.
        var planned = new List<Planned>();
        foreach (var s in stations)
        {
            var state = await client.GetAtisAsync(s.Id, ct).ConfigureAwait(false);

            var (disabledRules, frequencies) = RulesFor(s.Key[..4]);
            var icao = s.Key[..4].ToUpperInvariant();
            if (!builtInByAirport.TryGetValue(icao, out var builtIn))
                builtInByAirport[icao] = builtIn = BuiltInRules.PatternsFor(disabledRules);

            var stationRules = new List<KeyValuePair<string, string>>(builtIn);
            if (rules.TryGetValue(s.Key[..4], out var airportRules)) stationRules.AddRange(airportRules);
            if (removals is not null && removals.TryGetValue(s.Key, out var deleted))
                stationRules.AddRange(DeletionTracker.AsRemovalRules(deleted));

            var contractions = DatisFormatter.BuildContractions(
                await client.GetContractionsAsync(s.Key, ct).ConfigureAwait(false));
            var text = DatisFormatter.Format(s.Key, fetch.Entries, now, stationRules, contractions, frequencies);

            Diag($"{s.Key}: {(state.IsConnected ? "connected" : $"not connected ({state.NetworkConnectionStatus})")}, " +
                 $"real-world text {(text.IsAvailable ? $"available ({text.AirportConditions.Length}+{text.Notams.Length} chars)" : "not available")}, " +
                 $"vATIS has {state.AirportConditions.Length}+{state.Notams.Length} chars, " +
                 $"conditions {(state.AirportConditions.Trim() == text.AirportConditions.Trim() ? "identical" : "differ")}");

            // A live ATIS is never replaced by "not available": it keeps what it has until real text arrives.
            // A live ATIS is never replaced by "not available": it keeps what it has until real text arrives.
            if ((connectedOnly || (ask && state.IsConnected)) && !text.IsAvailable)
            {
                Record(s.Key, state.IsConnected, false);
                continue;
            }

            planned.Add(new Planned(s, state, text));
        }

        // Not the fill that follows Start: that one is something the controller asked for by pressing Start.
        var held = new HashSet<string>();
        if (ask)
        {
            foreach (var group in planned.GroupBy(p => AirportOf(p.Station.Key)))
                if (!await ApprovedAsync(group.Key, group.ToList(), ct).ConfigureAwait(false))
                    held.Add(group.Key);
        }

        foreach (var (s, state, text) in planned)
        {
            if (held.Contains(AirportOf(s.Key)))
            {
                Diag($"{s.Key}: held, nothing written");
                Record(s.Key, state.IsConnected, text.IsAvailable);
                continue;
            }

            if (state.IsConnected) letters.NoteConnected(s.Key);
            var sync = letters.ShouldSync(s.Key, text.AirportConditions, state.IsConnected);

            await client.ConfigureAtisAsync(s.Id, text.AirportConditions, text.Notams, sync, ct)
                .ConfigureAwait(false);
            if (sync) letters.MarkSynced(s.Key);
            Diag($"{s.Key}: written ({text.AirportConditions.Length}+{text.Notams.Length} chars, ATIS letter sync {sync})");

            Record(s.Key, state.IsConnected, text.IsAvailable, now);
        }
    }

    /// <summary>The disabled built-in rules and frequency handling for an airport: its own if it customizes them, else the defaults.</summary>
    private (IReadOnlyList<string> DisabledRules, IReadOnlyDictionary<FrequencyRole, FrequencySetting> Frequencies) RulesFor(string icao)
    {
        if (options.PerAirport.TryGetValue(icao.ToUpperInvariant(), out var own)) return (own.DisabledRules, own.Frequencies);
        return (options.DisabledRules, options.Frequencies);
    }

    // ---- approval ----

    private sealed record Planned(VatisStation Station, VatisAtisState State, FormattedAtis Text);

    /// <summary>The airport a station belongs to: the first four letters of its key (KMIA_A and KMIA_D are both KMIA).</summary>
    private static string AirportOf(string stationKey) => stationKey[..4].ToUpperInvariant();

    /// <summary>
    /// Decides whether a refresh may be written to an airport. An airport's stations are synced together or not at all,
    /// so this is one decision for all of them, connected or not: if any connected station would change in a way that
    /// needs approval, the controller is asked once, and the answer covers the whole airport. Only refreshes are checked.
    /// </summary>
    private async Task<bool> ApprovedAsync(string key, List<Planned> airport, CancellationToken ct)
    {
        if (options.Approval == ApprovalMode.Never || approver is null)
        {
            Diag($"{key}: approval off ({(approver is null ? "no approver" : "setting")}), writing without asking");
            return true;
        }

        // What triggers the question: a connected station whose runways or approaches would change, and nothing else.
        // What the question shows: every station of the airport that would change, since all of them are applied or
        // held together.
        var asks = false;
        var changes = new List<StationChange>();
        var fingerprints = new List<string>();
        foreach (var (station, state, text) in airport.OrderBy(p => p.Station.Key, StringComparer.Ordinal))
        {
            var current = state.AirportConditions.Trim();
            var updated = text.AirportConditions.Trim();
            if (current.Length == 0 || current == updated) continue; // nothing live to protect / nothing changes

            var flowChanged = FlowAnalyzer.FlowChanged(current, updated);
            if (state.IsConnected && flowChanged) asks = true;

            changes.Add(new StationChange(station.Key, current, updated, flowChanged,
                FlowAnalyzer.Analyze(current), FlowAnalyzer.Analyze(updated)));
            fingerprints.Add(station.Key + "|" + updated + "|" + text.Notams.Trim());
        }

        if (!asks)
        {
            _declined.Remove(key); // nothing needs approval, or vATIS and the real world agree again
            Diag($"{key}: no runway change on a connected station ({changes.Count} station(s) differ in other ways), writing without asking");
            return true;
        }

        var fingerprint = string.Join("\n", fingerprints);
        var request = new ApprovalRequest(key, changes);

        // Disregarded for the session: this is a change that would have asked, so it is not applied, but it is
        // written to the Log (once per new text) so the controller is never unaware that vATIS has fallen behind.
        if (_mutedForSession.Contains(key))
        {
            Diag($"{key}: runway change, but the airport is disregarded for the session; not applied");
            if (!_declined.TryGetValue(key, out var noted) || noted != fingerprint)
            {
                _declined[key] = fingerprint;
                Info(Messages.Get(Msg.EngineDisregardedForSession, new { station = key, summary = request.Summary }), LogKind.Warning);
            }
            return false;
        }

        // Declined before and the real-world text has not moved since: stay quiet and keep the current text.
        if (_declined.TryGetValue(key, out var seen) && seen == fingerprint)
        {
            Diag($"{key}: runway change already disregarded and the real-world text has not moved; not asking again");
            return false;
        }

        Diag($"{key}: runway change, asking the controller. {request.Summary}");
        Publish(SyncPhase.Syncing, Messages.Get(Msg.EngineWaitingApproval, new { station = key }));
        var decision = await approver.RequestAsync(request, ct).ConfigureAwait(false);
        Diag($"{key}: controller chose {decision}");

        switch (decision)
        {
            case ApprovalDecision.DisregardForSession:
                _mutedForSession.Add(key);
                goto case ApprovalDecision.Disregard;
            case ApprovalDecision.Disregard:
                _declined[key] = fingerprint;
                Info(Messages.Get(Msg.EngineApprovalKept, new { station = key, summary = request.Summary }));
                return false;
            default:
                _declined.Remove(key);
                Info(Messages.Get(Msg.EngineApprovalApplied, new { station = key, summary = request.Summary }), LogKind.Success);
                return true;
        }
    }

    // ---- connect / disconnect ----

    private async Task ConnectAsync(LetterSync letters, CancellationToken ct)
    {
        var all = await StatesAsync(ct).ConfigureAwait(false);
        var connected = all.Count(x => x.State.IsConnected);
        var candidates = all.Where(x => x.State.IsDisconnected).Select(x => x.Station).ToList();

        if (options.Airports.Count > 0)
        {
            var wanted = options.Airports.Select(a => a.ToUpperInvariant()).ToHashSet();
            candidates = candidates.Where(s => wanted.Contains(s.Key[..4].ToUpperInvariant())).ToList();
        }
        else if (options.AutoConnectActivePosition && positions.GetActivePosition() is { } pos &&
                 FollowedSuffixes.Contains(pos.Suffix))
        {
            candidates = candidates.Where(s => s.Key.Contains(pos.Prefix)).ToList();
        }
        else if (!options.ConnectProfileStations)
        {
            // Nothing selected, no position to follow, and the user turned profile connecting off.
            candidates = [];
        }
        // else: every D-ATIS station of the open profile, in the order vATIS lists them, up to the limit.

        foreach (var s in candidates)
        {
            if (connected >= options.MaxConnections) break;
            if (await client.ConnectAtisAsync(s.Id, ct).ConfigureAwait(false))
            {
                connected++;
                letters.NoteConnected(s.Key);
                Info(Messages.Get(Msg.EngineConnected, new { station = s.Key }), LogKind.Success);
            }
        }
    }

    private async Task DisconnectOverLimitAsync(CancellationToken ct)
    {
        var connected = (await StatesAsync(ct).ConfigureAwait(false))
            .Where(x => x.State.IsConnected).Select(x => x.Station).ToList();

        foreach (var s in connected.Skip(options.MaxConnections))
        {
            await client.DisconnectAtisAsync(s.Id, ct).ConfigureAwait(false);
            Info(Messages.Get(Msg.EngineDisconnectedLimit, new { station = s.Key, limit = options.MaxConnections }), LogKind.Warning);
        }
    }

    // ---- manual deletions ----

    private async Task<Dictionary<string, DeletionTracker.AtisText>> SnapshotAsync(CancellationToken ct)
    {
        var snapshot = new Dictionary<string, DeletionTracker.AtisText>();
        foreach (var (station, state) in await StatesAsync(ct).ConfigureAwait(false))
            if (state.IsConnected)
                snapshot[station.Key] = new(state.AirportConditions, state.Notams);
        return snapshot;
    }

    private async Task<Dictionary<string, List<string>>> BuildRemovalsAsync(
        Dictionary<string, DeletionTracker.AtisText> before, CancellationToken ct)
    {
        var after = await SnapshotAsync(ct).ConfigureAwait(false);
        var deletions = DeletionTracker.Compare(before, after);

        var result = new Dictionary<string, List<string>>();
        foreach (var (station, portions) in deletions)
        {
            var contractions = await client.GetContractionsAsync(station, ct).ConfigureAwait(false);
            result[station] = DeletionTracker.ExpandContractions(portions, contractions);
            Info(Messages.Get(Msg.EngineKeepingDeletions, new { count = result[station].Count, station }));
        }
        return result;
    }

    // ---- helpers ----

    private async Task<List<VatisStation>> DatisStationsAsync(CancellationToken ct) =>
        (await client.GetStationsAsync(ct).ConfigureAwait(false))
            .Where(s => s.HasDatisPreset && s.Key.Length >= 4)
            .ToList();

    private async Task<List<(VatisStation Station, VatisAtisState State)>> StatesAsync(CancellationToken ct)
    {
        var list = new List<(VatisStation, VatisAtisState)>();
        foreach (var s in await DatisStationsAsync(ct).ConfigureAwait(false))
            list.Add((s, await client.GetAtisAsync(s.Id, ct).ConfigureAwait(false)));
        return list;
    }

    /// <summary>Nothing disregarded carries over once the sync stops: the next start asks afresh.</summary>
    private void ClearPending()
    {
        _declined.Clear();
        _mutedForSession.Clear();
        _resyncs.Clear();
        lock (_gate)
            foreach (var key in _stations.Keys.ToList()) _stations[key] = _stations[key] with { Desynced = false };
    }

    private void Record(string key, bool connected, bool? available, DateTimeOffset? configuredAt = null)
    {
        lock (_gate)
        {
            _stations.TryGetValue(key, out var prev);
            _stations[key] = new StationStatus(
                key, connected, available ?? prev?.DatisAvailable ?? false, configuredAt ?? prev?.LastConfiguredAt)
            {
                // True only while a disregarded change is pending: it clears itself once vATIS matches the real world again.
                Desynced = _declined.ContainsKey(AirportOf(key)),
            };
        }
        Publish(_status.Phase, _status.Message, _status.LastSync, _status.NextSync, _status.IsError);
    }

    private void Publish(SyncPhase phase, string? message, DateTimeOffset? lastSync = null, DateTimeOffset? nextSync = null,
        bool isError = false)
    {
        if (phase != _status.Phase || (message is not null && message != _status.Message))
            Diag($"status: {phase}{(message is null ? "" : " - " + message)}{(isError ? " (problem)" : "")}");
        SyncStatus status;
        lock (_gate)
        {
            status = new SyncStatus(phase, message, lastSync ?? _status.LastSync, nextSync,
                _stations.Values.OrderBy(s => s.Key, StringComparer.Ordinal).ToList()) { IsError = isError };
            _status = status;
        }
        StatusChanged?.Invoke(this, status);
    }

    private void Info(string message, LogKind kind = LogKind.Info)
    {
        Log?.Invoke(message);
        LogEntry?.Invoke(message, kind);
    }

    /// <summary>
    /// Asks vATIS to apply the real-world ATIS letter once per station, and only during startup (the
    /// initial pass), once the station can accept it (syncAtisLetter is ignored until the station has
    /// connected to the ATIS hub). After startup the letter is never synced again: simulator weather
    /// changes on its own schedule, so the controller advances the letter in vATIS and it may legitimately
    /// differ from the real-world one.
    /// </summary>
    private sealed class LetterSync
    {
        private readonly HashSet<string> _synced = [];
        private readonly HashSet<string> _ready = [];
        private bool _startupDone;

        public void EndStartup() => _startupDone = true;

        public void NoteConnected(string station) => _ready.Add(station);
        public void MarkSynced(string station) => _synced.Add(station);

        public bool ShouldSync(string station, string airportConditions, bool connected) =>
            !_startupDone &&
            airportConditions != FormattedAtis.NotAvailable &&
            !_synced.Contains(station) &&
            (connected || _ready.Contains(station));
    }
}
