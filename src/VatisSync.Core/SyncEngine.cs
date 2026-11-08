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

    // Per sync session: text the controller declined (so it is not asked about again) and stations
    // they chose to stop being asked about.
    private readonly Dictionary<string, string> _declined = [];
    private readonly HashSet<string> _noMoreAsking = [];

    private SyncStatus _status = SyncStatus.Initial;
    private CancellationTokenSource? _cts;
    private Task? _run;

    public SyncStatus Status => _status;
    public bool IsRunning => _run is { IsCompleted: false };

    public event EventHandler<SyncStatus>? StatusChanged;
    public event Action<string>? Log;

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
                    Info($"vATIS unavailable: {ex.Message}");
                    Publish(SyncPhase.WaitingForVatis,
                        "Can't reach vATIS. Is it running with its websocket enabled? Retrying…");
                }
                catch (Exception ex)
                {
                    Info($"Unexpected error: {ex}");
                    Publish(SyncPhase.WaitingForVatis, $"Unexpected error: {ex.Message}. Retrying…");
                }

                try { await Task.Delay(options.RetryDelay, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }
        finally
        {
            Publish(SyncPhase.Stopped, null);
        }
    }

    /// <summary>One continuous stretch of talking to a running vATIS. Throws if vATIS goes away.</summary>
    private async Task RunSessionAsync(CancellationToken ct)
    {
        _stations.Clear();
        _declined.Clear();
        _noMoreAsking.Clear();
        var letters = new LetterSync();

        Publish(SyncPhase.Starting, "Starting…");
        if (options.LaunchVatis)
        {
            var launched = launcher.EnsureRunning();
            if (launched == LaunchResult.NotFound)
                Info("vATIS was not found in its default location; waiting for you to start it.");
        }

        Publish(SyncPhase.WaitingForVatis, "Waiting for vATIS…");
        if (!await client.WaitUntilReadyAsync(options.StartupTimeout, ct).ConfigureAwait(false))
            throw new VatisUnavailableException("vATIS did not respond.");

        // Initial pass. The first configure runs while stations are still disconnected, so vATIS
        // can't apply the ATIS letter yet; the second one (after connecting) sets it.
        Publish(SyncPhase.Syncing, "Loading D-ATIS…");
        await ConfigureAllAsync(connectedOnly: false, removals: null, letters, ct).ConfigureAwait(false);
        await ConnectAsync(letters, ct).ConfigureAwait(false);
        await ConfigureAllAsync(connectedOnly: false, removals: null, letters, ct).ConfigureAwait(false);
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
            await Task.Delay(options.Interval, ct).ConfigureAwait(false);
            Publish(SyncPhase.Syncing, "Refreshing D-ATIS…", lastSync);

            // After the first interval, treat text the user deleted by hand as "keep deleted".
            if (removals is null)
                removals = await BuildRemovalsAsync(snapshot, ct).ConfigureAwait(false);

            await ConfigureAllAsync(connectedOnly: true, removals, letters, ct).ConfigureAwait(false);
            lastSync = _time.GetUtcNow();
        }
    }

    // ---- configure ----

    private async Task ConfigureAllAsync(
        bool connectedOnly,
        Dictionary<string, List<string>>? removals,
        LetterSync letters,
        CancellationToken ct)
    {
        var stations = await DatisStationsAsync(ct).ConfigureAwait(false);
        var airports = stations.Select(s => s.Key[..4]).Distinct();
        var rules = await replacements.GetAsync(airports, ct).ConfigureAwait(false);
        var builtIn = BuiltInRules.PatternsFor(options.DisabledRules);

        var fetch = await datis.FetchAsync(ct).ConfigureAwait(false);
        if (!fetch.Succeeded)
            Info($"D-ATIS fetch failed ({fetch.Error}); " +
                 (fetch.FromCache ? "using the last good copy." : "no data available."));
        var now = _time.GetUtcNow();

        foreach (var s in stations)
        {
            var state = await client.GetAtisAsync(s.Id, ct).ConfigureAwait(false);

            var stationRules = new List<KeyValuePair<string, string>>(builtIn);
            if (rules.TryGetValue(s.Key[..4], out var airportRules)) stationRules.AddRange(airportRules);
            if (removals is not null && removals.TryGetValue(s.Key, out var deleted))
                stationRules.AddRange(DeletionTracker.AsRemovalRules(deleted));

            var contractions = DatisFormatter.BuildContractions(
                await client.GetContractionsAsync(s.Key, ct).ConfigureAwait(false));
            var text = DatisFormatter.Format(s.Key, fetch.Entries, now, stationRules, contractions, options.Frequencies);

            if (connectedOnly && !text.IsAvailable)
            {
                Record(s.Key, state.IsConnected, false);
                continue;
            }

            if (connectedOnly && state.IsConnected &&
                !await ApprovedAsync(s.Key, state, text, ct).ConfigureAwait(false))
            {
                Record(s.Key, state.IsConnected, text.IsAvailable);
                continue;
            }

            if (state.IsConnected) letters.NoteConnected(s.Key);
            var sync = letters.ShouldSync(s.Key, text.AirportConditions, state.IsConnected);

            await client.ConfigureAtisAsync(s.Id, text.AirportConditions, text.Notams, sync, ct)
                .ConfigureAwait(false);
            if (sync) letters.MarkSynced(s.Key);

            Record(s.Key, state.IsConnected, text.IsAvailable, now);
        }
    }

    // ---- approval ----

    /// <summary>
    /// Decides whether a refresh may be written to a connected station. Only refreshes are checked: the
    /// startup pass is something the controller asked for by pressing Start.
    /// </summary>
    private async Task<bool> ApprovedAsync(string key, VatisAtisState state, FormattedAtis text, CancellationToken ct)
    {
        if (options.Approval == ApprovalMode.Never || approver is null) return true;
        if (_noMoreAsking.Contains(key)) return true;

        var current = state.AirportConditions.Trim();
        var updated = text.AirportConditions.Trim();
        if (current.Length == 0 || current == updated) return true; // nothing live to protect / nothing changes

        var flowChanged = FlowAnalyzer.FlowChanged(current, updated);
        if (options.Approval == ApprovalMode.RunwayChanges && !flowChanged) return true;

        // Declined before and the real-world text has not moved since: stay quiet and keep the current text.
        var fingerprint = updated + "|" + text.Notams.Trim();
        if (_declined.TryGetValue(key, out var seen) && seen == fingerprint) return false;

        Publish(SyncPhase.Syncing, $"Waiting for your approval ({key})…");
        var request = new ApprovalRequest(key, current, updated, flowChanged,
            FlowAnalyzer.Analyze(current), FlowAnalyzer.Analyze(updated));
        var decision = await approver.RequestAsync(request, ct).ConfigureAwait(false);

        switch (decision)
        {
            case ApprovalDecision.KeepCurrent:
                _declined[key] = fingerprint;
                Info($"{key}: kept the current ATIS ({request.Summary})");
                return false;
            case ApprovalDecision.ApplyAndStopAsking:
                _noMoreAsking.Add(key);
                goto default;
            default:
                _declined.Remove(key);
                Info($"{key}: update approved ({request.Summary})");
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
                Info($"Connected {s.Key}.");
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
            Info($"Disconnected {s.Key} (over the {options.MaxConnections}-connection limit).");
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
            Info($"Keeping {result[station].Count} manual deletion(s) for {station}.");
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

    private void Record(string key, bool connected, bool? available, DateTimeOffset? configuredAt = null)
    {
        lock (_gate)
        {
            _stations.TryGetValue(key, out var prev);
            _stations[key] = new StationStatus(
                key, connected, available ?? prev?.DatisAvailable ?? false, configuredAt ?? prev?.LastConfiguredAt);
        }
        Publish(_status.Phase, _status.Message, _status.LastSync, _status.NextSync);
    }

    private void Publish(SyncPhase phase, string? message, DateTimeOffset? lastSync = null, DateTimeOffset? nextSync = null)
    {
        SyncStatus status;
        lock (_gate)
        {
            status = new SyncStatus(phase, message, lastSync ?? _status.LastSync, nextSync,
                _stations.Values.OrderBy(s => s.Key, StringComparer.Ordinal).ToList());
            _status = status;
        }
        StatusChanged?.Invoke(this, status);
    }

    private void Info(string message) => Log?.Invoke(message);

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
