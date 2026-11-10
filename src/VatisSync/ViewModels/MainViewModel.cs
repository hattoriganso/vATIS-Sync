using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VatisSync.Core;
using VatisSync.Services;

namespace VatisSync.ViewModels;

public sealed record ApprovalChoice(ApprovalMode Mode, string Label);

public sealed partial class MainViewModel : ObservableObject
{
    private const int MaxLogLines = 500;

    private readonly SettingsStore _store;
    private readonly Func<SyncOptions, SyncEngine> _engineFactory;
    private readonly Action<bool> _applyTray;
    private readonly ProfileDiscovery _discovery;
    private readonly AirportFileStore _airportFiles;
    private readonly DatisPreviewBuilder _preview;
    private SyncEngine? _engine;
    private readonly ActivityLog? _activityLog;
    private AppSettings _settings;
    private bool _loading = true;

    public static IReadOnlyList<ApprovalChoice> ApprovalChoices { get; } =
    [
        new(ApprovalMode.RunwayChanges, "Ask me when the runways change"),
        new(ApprovalMode.Never, "Apply runway changes automatically"),
    ];

    public static IReadOnlyList<FrequencyModeChoice> FrequencyModeChoices => FrequencySetEditor.ModeChoices;

    public MainViewModel(Func<SyncOptions, SyncEngine>? engineFactory = null, Action<bool>? applyTray = null,
        IUpdateApprover? approver = null,
        Func<CancellationToken, Task<IReadOnlyList<VatisStation>>>? stationsSource = null,
        SettingsStore? store = null,
        AirportFileStore? airportFiles = null,
        DatisPreviewBuilder? preview = null,
        ActivityLog? activityLog = null)
    {
        _activityLog = activityLog;
        Banners = new ListCollectionView(_notices.Items) { Filter = n => ((Notice)n).Kind != NoticeKind.Info };
        Toasts = new ListCollectionView(_notices.Items) { Filter = n => ((Notice)n).Kind == NoticeKind.Info };
        _preview = preview ?? EngineFactory.CreatePreviewBuilder();
        _store = store ?? new SettingsStore();
        _airportFiles = airportFiles ?? new AirportFileStore();
        _airportFiles.Warning += m => OnUi(() => { AddLog(m, LogKind.Warning); Notify("airports-folder", NoticeKind.Warning, m); });
        _discovery = new ProfileDiscovery(stationsSource ?? EngineFactory.GetProfileStationsAsync);
        _discovery.StateChanged += state => OnUi(() => OnDiscoveryState(state));
        _discovery.AirportsFound += airports => OnUi(() => OnAirportsFound(airports));
        _discovery.StationsFound += stations => OnUi(() => OnStationsFound(stations));
        _discovery.StationsWithoutPreset += stations => OnUi(() =>
            AddLog(Messages.Get(Msg.DiscoveryNoPreset, new { count = stations.Count, stations = string.Join(", ", stations) }), LogKind.Warning));
        _engineFactory = engineFactory ?? (o => EngineFactory.Create(o, approver));
        _applyTray = applyTray ?? (_ => { });

        _store.Warning += m => OnUi(() => { AddLog(m, LogKind.Warning); Notify("settings-file", NoticeKind.Warning, m); });
        EngineFactory.ReplacementStore.Warning += m => OnUi(() => { AddLog(m, LogKind.Warning); Notify("rules-file", NoticeKind.Warning, m); });
        _settings = _store.Load();

        IntervalMinutes = _settings.IntervalMinutes;
        MaxConnections = _settings.MaxConnections;
        AutoConnectActivePosition = _settings.AutoConnectActivePosition;
        ConnectProfileStations = _settings.ConnectProfileStations;
        LaunchVatis = _settings.LaunchVatis;
        Approval = _settings.Approval;
        DetailedLog = _settings.DetailedLog;
        StartSyncOnLaunch = _settings.StartSyncOnLaunch;
        StartMinimized = _settings.StartMinimized;
        CloseToTray = _settings.CloseToTray;
        // The registry is the truth for autostart: the user may have removed it in Task Manager.
        StartWithWindows = StartupRegistration.IsEnabled();

        foreach (var a in _settings.Airports) ConnectAirports.Add(a);

        DefaultRules = new RuleSetEditor(_settings.DisabledRules, Save);
        DefaultFrequencies = new FrequencySetEditor(_settings.Frequencies, Save);

        MigrateLegacyAirportSettings();
        _selectedPage = Overview; // the airport list starts empty: it is filled only from what vATIS reports

        Airports.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasAirports));
            OnPropertyChanged(nameof(HasNoAirports));
        };

        _loading = false;
        ApplyStatus(SyncStatus.Initial);
    }

    // ---- airport list ----

    public OverviewPage Overview { get; } = new();

    /// <summary>The airports in alphabetical order. The Overview is not one of them: it has its own tile above the list.</summary>
    public ObservableCollection<AirportViewModel> Airports { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedAirport), nameof(IsOverviewSelected), nameof(IsAirportSelected))]
    private object _selectedPage;

    /// <summary>The airport being shown, or null while the Overview is. Setting null (the list losing its selection) is ignored.</summary>
    partial void OnSelectedPageChanged(object value)
    {
        if (value is AirportViewModel airport) airport.Summary.EnsureFresh();
    }

    public AirportViewModel? SelectedAirport
    {
        get => SelectedPage as AirportViewModel;
        set { if (value is not null) SelectedPage = value; }
    }

    public bool IsOverviewSelected => SelectedPage is OverviewPage;
    public bool IsAirportSelected => SelectedPage is AirportViewModel;

    [RelayCommand]
    private void ShowOverview() => SelectedPage = Overview;

    public bool HasAirports => Airports.Count > 0;
    public bool HasNoAirports => Airports.Count == 0;

    // ---- messages along the bottom of the window ----

    private const string VatisUnreachable = "vatis-unreachable";
    private const string Discovery = "vatis-discovery";
    private const string DiscoveryEmpty = "vatis-discovery-empty";
    private const string SettingsNoteKey = "settings-apply-next-start";
    private static readonly TimeSpan InfoLifetime = TimeSpan.FromSeconds(6);

    private readonly NoticeBoard _notices = new();

    /// <summary>What the bottom bar shows. Errors and warnings stay until dismissed or resolved; confirmations clear themselves.</summary>
    public ObservableCollection<Notice> Notices => _notices.Items;

    /// <summary>Errors and warnings: a banner along the bottom that stays put.</summary>
    public ICollectionView Banners { get; }

    /// <summary>Confirmations: toasts floating over the content, so the layout never moves.</summary>
    public ICollectionView Toasts { get; }

    private static readonly TimeSpan FadeTime = TimeSpan.FromMilliseconds(250);

    /// <summary>Shows a message in the bar. Confirmations (<see cref="NoticeKind.Info"/>) clear themselves after a few seconds.</summary>
    private void Notify(string key, NoticeKind kind, string message)
    {
        _activityLog?.Write($"notice shown ({kind}, {key}): {message}",
            kind == NoticeKind.Error ? LogKind.Error : kind == NoticeKind.Warning ? LogKind.Warning : LogKind.Info);
        var notice = _notices.Post(key, kind, message);
        if (kind != NoticeKind.Info) return;
        _ = Task.Delay(InfoLifetime - FadeTime).ContinueWith(_ => OnUi(() => notice.IsFading = true), TaskScheduler.Default);
        _ = Task.Delay(InfoLifetime).ContinueWith(_ => OnUi(() => _notices.Dismiss(notice)), TaskScheduler.Default);
    }

    /// <summary>
    /// Tells the user about an error nothing else caught. The app carries on; the full details are in the error log.
    /// Safe to call from any thread.
    /// </summary>
    public void ReportUnexpectedError(Exception error, string? logPath)
    {
        var firstLine = error.Message.Split('\n')[0].Trim();
        OnUi(() =>
        {
            AddLog(Messages.Get(Msg.AppUnexpectedLog, new { type = error.GetType().Name, message = firstLine }), LogKind.Error);
            Notify("unexpected-error", NoticeKind.Error,
                logPath is null ? Messages.Get(Msg.AppUnexpectedNoFile) : Messages.Get(Msg.AppUnexpected, new { path = logPath }));
        });
    }

    [RelayCommand]
    private void DismissNotice(Notice? notice)
    {
        if (notice is not null) _notices.Dismiss(notice);
    }

    // ---- the airports come from the open vATIS profile ----

    private bool _discoveryAnnounced; // the warning has been shown once in this run of failures, so it is not re-shown if closed
    private bool _manualRefresh;      // the user pressed the button, so say what the re-read found even when nothing changed
    private int _loggedFailures;      // failed attempts already written to the log in this run of failures

    [ObservableProperty] private string _discoveryText = Messages.Get(Msg.DiscoverySearching);
    [ObservableProperty] private string _discoveryButtonText = "Retry now";
    /// <summary>True once the vATIS profile has been read; the sidebar shows a green dot for it, amber while still looking.</summary>
    [ObservableProperty] private bool _discoveryFound;

    /// <summary>Starts reading the vATIS profile, and keeps trying until vATIS answers. Read-only towards vATIS.</summary>
    public void StartDiscovery() => _discovery.Start();

    public Task StopDiscoveryAsync() => _discovery.StopAsync();

    /// <summary>Tries now instead of waiting for the countdown (which then restarts if it fails), or re-reads the profile after a success.</summary>
    [RelayCommand]
    private void RetryDiscovery()
    {
        _manualRefresh = true;
        _discovery.RetryNow();
    }

    private void OnDiscoveryState(DiscoveryState state)
    {
        DiscoveryFound = state.Phase == DiscoveryPhase.Found;
        switch (state.Phase)
        {
            case DiscoveryPhase.Searching:
                DiscoveryText = Messages.Get(state.Failures == 0 ? Msg.DiscoverySearching : Msg.DiscoverySearchingAgain);
                if (state.Failures > 0) _notices.Update(Discovery, Messages.Get(Msg.DiscoveryTryingBar));
                break;

            case DiscoveryPhase.Waiting:
                var seconds = Math.Max(1, (int)Math.Ceiling(state.Remaining.TotalSeconds));
                DiscoveryText = Messages.Get(Msg.DiscoveryWaiting, new { seconds });
                DiscoveryButtonText = "Retry now";
                var bar = Messages.Get(Msg.DiscoveryWaitingBar, new { seconds });
                if (state.Failures > _loggedFailures) // the first tick after each failed attempt: log every attempt, once
                {
                    _loggedFailures = state.Failures;
                    AddLog(Messages.Get(Msg.DiscoveryAttemptFailed,
                        new { problem = state.Problem ?? Messages.Get(Msg.DiscoveryProblem), attempt = state.Failures, seconds }), LogKind.Warning);
                }
                if (!_discoveryAnnounced)
                {
                    _discoveryAnnounced = true;
                    Notify(Discovery, NoticeKind.Warning, bar);
                }
                else _notices.Update(Discovery, bar); // only if the user has not closed it
                break;

            case DiscoveryPhase.Found:
                _discoveryAnnounced = false;
                _notices.Resolve(Discovery);
                DiscoveryText = Messages.Get(Msg.DiscoveryFound);
                DiscoveryButtonText = "Refresh";
                break;
        }
    }

    /// <summary>Each airport's stations from the profile (KMIA_A and KMIA_D), which the Summary tab is about.</summary>
    private void OnStationsFound(IReadOnlyList<VatisStation> stations)
    {
        var byAirport = stations
            .GroupBy(s => s.Name[..4].ToUpperInvariant())
            .ToDictionary(g => g.Key, g => (IReadOnlyList<string>)g.Select(s => s.Key).Distinct().Order(StringComparer.Ordinal).ToList());
        foreach (var page in Airports)
        {
            page.ProfileStations = byAirport.GetValueOrDefault(page.Icao) ?? [];
            page.Summary.MarkDirty();
        }
        SelectedAirport?.Summary.EnsureFresh();
    }

    private void OnAirportsFound(IReadOnlyList<string> found)
    {
        var manual = _manualRefresh;
        _manualRefresh = false;

        // Whatever vATIS reported, an airport it did not report is no longer in the profile. This includes a profile with no
        // D-ATIS stations at all: every listed airport is then stale, and the warning below explains why.
        foreach (var page in Airports) page.IsStale = !found.Contains(page.Icao);

        if (found.Count == 0)
        {
            _loggedFailures = 0;
            var message = Messages.Get(Msg.DiscoveryEmpty);
            AddLog(message, LogKind.Warning);
            Notify(DiscoveryEmpty, NoticeKind.Warning, message);
            return;
        }
        _notices.Resolve(DiscoveryEmpty);

        var added = found.Where(icao => Airports.All(a => a.Icao != icao)).ToList();
        var failedBefore = _loggedFailures;
        _loggedFailures = 0;
        AddLog(Messages.Get(Msg.DiscoveryRead, new { count = found.Count, @new = added.Count, failed = failedBefore }), LogKind.Success);
        foreach (var icao in added) InsertAirport(NewAirportPage(icao));

        var stale = Airports.Count(a => a.IsStale);
        string? result = null;
        if (added.Count > 0)
            result = Messages.Get(Msg.DiscoveryAdded, new { count = added.Count });
        else if (manual)
            result = stale > 0
                ? Messages.Get(Msg.DiscoveryStale, new { count = stale })
                : Messages.Get(Msg.DiscoveryMatches);
        if (result is null) return;
        Notify(Discovery + "-result", NoticeKind.Info, result);
    }

    private AirportViewModel NewAirportPage(string icao)
    {
        AirportViewModel? page = null;
        page = new AirportViewModel(
            icao, _airportFiles.Load(icao),
            () => DefaultRules.Disabled, () => DefaultFrequencies.ToDictionary(),
            () => SaveAirport(page!),
            (keys, rules, force, ct) => _preview.BuildAsync(keys, rules, force, ct));
        return page;
    }

    /// <summary>Writes this airport's own settings to its file in the Airports folder.</summary>
    private void SaveAirport(AirportViewModel page)
    {
        if (_loading) return;
        if (_airportFiles.Save(page.Icao, page.ToSettings()))
            SettingsNote = IsRunning ? Messages.Get(Msg.SettingsApplyNextStart) : "";

        // The summary shows the effect of these settings, so it follows them.
        page.Summary.MarkDirty();
        if (ReferenceEquals(page, SelectedAirport)) page.Summary.ScheduleRefresh();
    }

    /// <summary>
    /// Older versions kept the airport list and each airport's settings in settings.json. The per-airport settings move into
    /// the Airports folder (files that already exist are left alone) and both are cleared from settings.json.
    /// </summary>
    private void MigrateLegacyAirportSettings()
    {
        if (_settings.AirportOverrides.Count == 0 && _settings.KnownAirports.Count == 0) return;
        var imported = _airportFiles.ImportMissing(_settings.AirportOverrides);
        if (imported > 0) AddLog(Messages.Get(Msg.AirportsImported, new { count = imported }), LogKind.Success);
        _settings = _settings with { AirportOverrides = [], KnownAirports = [] };
        _store.Save(_settings);
    }

    private void InsertAirport(AirportViewModel page)
    {
        var index = 0;
        while (index < Airports.Count && string.CompareOrdinal(Airports[index].Icao, page.Icao) < 0) index++;
        Airports.Insert(index, page);
    }

    /// <summary>Takes an airport off the list for this session. Its settings file is kept; it comes back when vATIS reports it.</summary>
    [RelayCommand]
    private void RemoveAirport(AirportViewModel? page)
    {
        if (page is null) return;
        if (SelectedPage == page) SelectedPage = Overview;
        Airports.Remove(page);
    }

    private static bool IsIcao(string s) => s.Length == 4 && s.All(char.IsLetterOrDigit);

    // ---- defaults (edited in the Settings window) ----

    public RuleSetEditor DefaultRules { get; }
    public FrequencySetEditor DefaultFrequencies { get; }

    // ---- status ----

    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private string _headline = "";
    [ObservableProperty] private string _detail = "";
    [ObservableProperty] private string _lastSync = "Never";
    [ObservableProperty] private string _nextSync = "";
    [ObservableProperty] private string _startStopText = "Start";
    [ObservableProperty] private string _settingsNote = "";

    /// <summary>The "applies next time you press Start" note also shows as a banner, so the header never grows and moves the page.</summary>
    partial void OnSettingsNoteChanged(string value)
    {
        if (value.Length == 0) _notices.Resolve(SettingsNoteKey);
        else Notify(SettingsNoteKey, NoticeKind.Warning, value);
    }

    public ObservableCollection<StationRow> Stations { get; } = [];
    public ObservableCollection<LogLine> LogLines { get; } = [];

    // ---- settings ----

    [ObservableProperty] private int _intervalMinutes;
    [ObservableProperty] private int _maxConnections;
    [ObservableProperty] private bool _autoConnectActivePosition;
    [ObservableProperty] private bool _connectProfileStations;
    [ObservableProperty] private bool _launchVatis;
    [ObservableProperty] private ApprovalMode _approval = ApprovalMode.RunwayChanges;
    [ObservableProperty] private bool _detailedLog = true;
    [ObservableProperty] private bool _startSyncOnLaunch;
    [ObservableProperty] private bool _startWithWindows;
    [ObservableProperty] private bool _startMinimized;
    [ObservableProperty] private bool _closeToTray;
    [ObservableProperty] private string _newConnectAirport = "";
    [ObservableProperty] private string? _selectedConnectAirport;

    /// <summary>"Connect only these airports": the global list, separate from the airport list on the left.</summary>
    public ObservableCollection<string> ConnectAirports { get; } = [];

    partial void OnIntervalMinutesChanged(int value) => Save();
    partial void OnMaxConnectionsChanged(int value) => Save();
    partial void OnAutoConnectActivePositionChanged(bool value) => Save();
    partial void OnConnectProfileStationsChanged(bool value) => Save();
    partial void OnLaunchVatisChanged(bool value) => Save();
    partial void OnApprovalChanged(ApprovalMode value) => Save();

    partial void OnDetailedLogChanged(bool value)
    {
        if (_activityLog is not null) _activityLog.TraceEnabled = value;
        Save();
    }
    partial void OnStartSyncOnLaunchChanged(bool value) => Save();
    partial void OnStartMinimizedChanged(bool value) => Save();
    partial void OnCloseToTrayChanged(bool value) => Save();

    partial void OnStartWithWindowsChanged(bool value)
    {
        if (_loading) return;
        if (!StartupRegistration.Set(value))
        {
            AddLog(Messages.Get(Msg.StartupFailed), LogKind.Error);
            _loading = true;
            StartWithWindows = StartupRegistration.IsEnabled();
            _loading = false;
        }
        Save();
    }

    /// <summary>True the first time only: the caller should tell the user the app is still in the tray.</summary>
    public bool TakeTrayHint()
    {
        if (_settings.TrayHintShown) return false;
        _settings = _settings with { TrayHintShown = true };
        _store.Save(_settings);
        return true;
    }

    private AppSettings Current => new()
    {
        TrayHintShown = _settings.TrayHintShown,
        IntervalMinutes = IntervalMinutes,
        MaxConnections = MaxConnections,
        Airports = [.. ConnectAirports],
        AutoConnectActivePosition = AutoConnectActivePosition,
        ConnectProfileStations = ConnectProfileStations,
        LaunchVatis = LaunchVatis,
        Approval = Approval,
        DetailedLog = DetailedLog,
        DisabledRules = DefaultRules.Disabled,
        Frequencies = DefaultFrequencies.ToDictionary(),
        StartSyncOnLaunch = StartSyncOnLaunch,
        StartWithWindows = StartWithWindows,
        StartMinimized = StartMinimized,
        CloseToTray = CloseToTray,
    };

    private void Save()
    {
        if (_loading) return;
        _settings = Current.Normalized();
        _store.Save(_settings);
        _applyTray(CloseToTray);

        // Airports that follow the defaults show their effect in the summary, so every summary is out of date now.
        foreach (var airport in Airports) airport.Summary.MarkDirty();
        SelectedAirport?.Summary.ScheduleRefresh();
        SettingsNote = IsRunning ? Messages.Get(Msg.SettingsApplyNextStart) : "";
    }

    // ---- connect list ----

    [RelayCommand]
    private void AddConnectAirport()
    {
        var icao = NewConnectAirport.Trim().ToUpperInvariant();
        if (!IsIcao(icao))
        {
            AddLog(Messages.Get(Msg.ConnectInvalidAirport, new { text = NewConnectAirport.Trim() }), LogKind.Warning);
            return;
        }
        if (!ConnectAirports.Contains(icao)) { ConnectAirports.Add(icao); Save(); }
        NewConnectAirport = "";
    }

    [RelayCommand]
    private void RemoveConnectAirport()
    {
        if (SelectedConnectAirport is null) return;
        ConnectAirports.Remove(SelectedConnectAirport);
        Save();
    }

    // ---- start / stop ----

    public bool ShouldAutoStart => StartSyncOnLaunch;
    public bool ShouldStartHidden => StartMinimized;

    [RelayCommand]
    private async Task StartStop()
    {
        if (_engine is { IsRunning: true })
        {
            await StopAsync();
            return;
        }

        var engine = _engineFactory(OptionsForEngine());
        engine.StatusChanged += (_, s) => OnUi(() => ApplyStatus(s));
        engine.LogEntry += (m, kind) => OnUi(() => AddLog(m, kind));
        _engine = engine;
        engine.Start();
        SettingsNote = "";
    }

    /// <summary>Takes back a Disregard for an airport: checks the real-world ATIS now and asks about any change again.</summary>
    [RelayCommand]
    private void ResyncAirport(AirportViewModel? airport)
    {
        if (airport is not null) _engine?.Resync(airport.Icao);
    }

    /// <summary>The general settings plus every airport's own settings from the Airports folder (listed or not).</summary>
    private SyncOptions OptionsForEngine()
    {
        var options = _settings.ToSyncOptions();
        var perAirport = _airportFiles.LoadAll().ToDictionary(kv => kv.Key, kv => kv.Value.Resolve(_settings));
        return options with { PerAirport = perAirport };
    }

    public async Task StopAsync()
    {
        var engine = _engine;
        if (engine is null) return;
        await engine.StopAsync();
        ApplyStatus(engine.Status);
    }

    public void StartIfConfigured()
    {
        if (ShouldAutoStart && _engine is not { IsRunning: true }) StartStopCommand.Execute(null);
    }

    private void ApplyStatus(SyncStatus s)
    {
        IsRunning = s.Phase != SyncPhase.Stopped;
        StartStopText = IsRunning ? "Stop" : "Start";
        (Headline, Detail) = Describe(s);
        LastSync = s.LastSync is { } l ? l.ToLocalTime().ToString("HH:mm:ss") : "Never";
        NextSync = s.NextSync is { } n && s.Phase == SyncPhase.Idle ? n.ToLocalTime().ToString("HH:mm:ss") : "";

        var rows = s.Stations.Select(st => new StationRow(
            st.Key,
            st.Connected ? "Connected" : "Not connected",
            !st.Connected ? "" : st.DatisAvailable ? "D-ATIS loaded" : "No current D-ATIS",
            st.LastConfiguredAt is { } t ? t.ToLocalTime().ToString("HH:mm:ss") : "",
            Connected: st.Connected,
            Synced: st.LastConfiguredAt is not null && st.DatisAvailable,
            Desynced: st.Desynced)).ToList();

        // A sync problem stays up until the sync is working again (or stopped), not just until the next retry starts.
        if (s.IsError && s.Message is { } problem) Notify(VatisUnreachable, NoticeKind.Error, problem);
        else if (s.Phase is SyncPhase.Syncing or SyncPhase.Idle or SyncPhase.Stopped) _notices.Resolve(VatisUnreachable);

        Stations.Clear();
        foreach (var r in rows) Stations.Add(r);

        // Airports found in the open vATIS profile join the list (and are remembered for next time).
        foreach (var icao in rows.Select(r => r.Key[..4].ToUpperInvariant()).Distinct())
        {
            if (Airports.Any(a => a.Icao == icao)) continue;
            InsertAirport(NewAirportPage(icao));
        }
        foreach (var page in Airports)
        {
            var mine = rows.Where(r => string.Equals(r.Key[..4], page.Icao, StringComparison.OrdinalIgnoreCase)).ToList();
            page.UpdateStations(mine);
            if (mine.Count > 0) page.IsStale = false;
        }
    }

    private static (string Headline, string Detail) Describe(SyncStatus s) => s.Phase switch
    {
        SyncPhase.Stopped => (Messages.Get(Msg.StatusStoppedHeadline), Messages.Get(Msg.StatusStoppedDetail)),
        SyncPhase.Starting => (Messages.Get(Msg.StatusStartingHeadline), s.Message ?? Messages.Get(Msg.StatusStartingDetail)),
        SyncPhase.WaitingForVatis => (Messages.Get(Msg.StatusWaitingHeadline), s.Message ?? Messages.Get(Msg.StatusWaitingDetail)),
        SyncPhase.Syncing => (Messages.Get(Msg.StatusSyncingHeadline), s.Message ?? Messages.Get(Msg.StatusSyncingDetail)),
        SyncPhase.Idle => (Messages.Get(Msg.StatusIdleHeadline), s.Message ?? Messages.Get(Msg.StatusIdleDetail)),
        _ => ("", ""),
    };

    // ---- helpers ----

    [RelayCommand]
    private void OpenRulesFile()
    {
        var path = LocalReplacementStore.DefaultUserFilePath;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (!File.Exists(path))
                File.WriteAllText(path, "{\n  // Your own cleanup rules. See the project docs (docs/rules.md) for the format.\n  \"replacements\": {}\n}\n");
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            AddLog(Messages.Get(Msg.RulesOpenFailed, new { path, reason = ex.Message }), LogKind.Error);
        }
    }

    [RelayCommand]
    private void ClearLog() => LogLines.Clear();

    /// <summary>Opens the folder holding the log files in Explorer.</summary>
    [RelayCommand]
    private void OpenLogFolder()
    {
        var folder = _activityLog?.Directory ?? ActivityLog.DefaultDirectory;
        try
        {
            Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AddLog(Messages.Get(Msg.LogFolderOpenFailed, new { path = folder, reason = ex.Message }), LogKind.Warning);
        }
    }

    private void AddLog(string message, LogKind kind = LogKind.Info)
    {
        _activityLog?.Write(message, kind);
        LogLines.Add(new LogLine($"{DateTime.Now:HH:mm:ss}  {message}", kind));
        while (LogLines.Count > MaxLogLines) LogLines.RemoveAt(0);
    }

    private static void OnUi(Action action)
    {
        var d = Application.Current?.Dispatcher;
        if (d is null || d.CheckAccess()) action();
        else d.BeginInvoke(action);
    }
}
