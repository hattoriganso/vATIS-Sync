using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VatisSync.Core;
using VatisSync.Services;

namespace VatisSync.ViewModels;

public sealed record ApprovalChoice(ApprovalMode Mode, string Label);

public sealed partial class MainViewModel : ObservableObject
{
    private const int MaxLogLines = 500;

    private readonly SettingsStore _store = new();
    private readonly Func<SyncOptions, SyncEngine> _engineFactory;
    private readonly Action<bool> _applyTray;
    private SyncEngine? _engine;
    private AppSettings _settings;
    private bool _loading = true;

    public static IReadOnlyList<ApprovalChoice> ApprovalChoices { get; } =
    [
        new(ApprovalMode.RunwayChanges, "Ask me when the runways change"),
        new(ApprovalMode.Always, "Ask me before any change"),
        new(ApprovalMode.Never, "Apply changes automatically"),
    ];

    public static IReadOnlyList<FrequencyModeChoice> FrequencyModeChoices => FrequencySetEditor.ModeChoices;

    public MainViewModel(Func<SyncOptions, SyncEngine>? engineFactory = null, Action<bool>? applyTray = null,
        IUpdateApprover? approver = null)
    {
        _engineFactory = engineFactory ?? (o => EngineFactory.Create(o, approver));
        _applyTray = applyTray ?? (_ => { });

        _store.Warning += m => OnUi(() => AddLog(m));
        EngineFactory.ReplacementStore.Warning += m => OnUi(() => AddLog(m));
        _settings = _store.Load();

        IntervalMinutes = _settings.IntervalMinutes;
        MaxConnections = _settings.MaxConnections;
        AutoConnectActivePosition = _settings.AutoConnectActivePosition;
        ConnectProfileStations = _settings.ConnectProfileStations;
        LaunchVatis = _settings.LaunchVatis;
        Approval = _settings.Approval;
        StartSyncOnLaunch = _settings.StartSyncOnLaunch;
        StartMinimized = _settings.StartMinimized;
        CloseToTray = _settings.CloseToTray;
        // The registry is the truth for autostart: the user may have removed it in Task Manager.
        StartWithWindows = StartupRegistration.IsEnabled();

        foreach (var a in _settings.Airports) ConnectAirports.Add(a);

        DefaultRules = new RuleSetEditor(_settings.DisabledRules, Save);
        DefaultFrequencies = new FrequencySetEditor(_settings.Frequencies, Save);

        var listed = _settings.KnownAirports.Concat(_settings.AirportOverrides.Keys).Concat(_settings.Airports)
            .Distinct().Order(StringComparer.Ordinal);
        foreach (var icao in listed) InsertAirport(NewAirportPage(icao));
        _selectedPage = Overview;

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
    public AirportViewModel? SelectedAirport
    {
        get => SelectedPage as AirportViewModel;
        set { if (value is not null) SelectedPage = value; }
    }

    public bool IsOverviewSelected => SelectedPage is OverviewPage;
    public bool IsAirportSelected => SelectedPage is AirportViewModel;

    [RelayCommand]
    private void ShowOverview() => SelectedPage = Overview;

    [ObservableProperty] private string _newAirport = "";

    private AirportViewModel NewAirportPage(string icao) => new(
        icao, _settings.AirportOverrides.GetValueOrDefault(icao),
        () => DefaultRules.Disabled, () => DefaultFrequencies.ToDictionary(), Save);

    private void InsertAirport(AirportViewModel page)
    {
        var index = 0;
        while (index < Airports.Count && string.CompareOrdinal(Airports[index].Icao, page.Icao) < 0) index++;
        Airports.Insert(index, page);
    }

    [RelayCommand]
    private void AddAirport()
    {
        var icao = NewAirport.Trim().ToUpperInvariant();
        if (!IsIcao(icao))
        {
            AddLog($"'{NewAirport.Trim()}' is not a 4-letter airport code (for example KSFO).");
            return;
        }
        var page = Airports.FirstOrDefault(a => a.Icao == icao);
        if (page is null)
        {
            page = NewAirportPage(icao);
            InsertAirport(page);
            Save();
        }
        SelectedPage = page;
        NewAirport = "";
    }

    /// <summary>Drops an airport from the list and forgets its own settings. A profile airport returns on the next sync.</summary>
    [RelayCommand]
    private void RemoveAirport(AirportViewModel? page)
    {
        if (page is null) return;
        if (SelectedPage == page) SelectedPage = Overview;
        Airports.Remove(page);
        Save();
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

    public ObservableCollection<StationRow> Stations { get; } = [];
    public ObservableCollection<string> LogLines { get; } = [];

    // ---- settings ----

    [ObservableProperty] private int _intervalMinutes;
    [ObservableProperty] private int _maxConnections;
    [ObservableProperty] private bool _autoConnectActivePosition;
    [ObservableProperty] private bool _connectProfileStations;
    [ObservableProperty] private bool _launchVatis;
    [ObservableProperty] private ApprovalMode _approval = ApprovalMode.RunwayChanges;
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
    partial void OnStartSyncOnLaunchChanged(bool value) => Save();
    partial void OnStartMinimizedChanged(bool value) => Save();
    partial void OnCloseToTrayChanged(bool value) => Save();

    partial void OnStartWithWindowsChanged(bool value)
    {
        if (_loading) return;
        if (!StartupRegistration.Set(value))
        {
            AddLog("Could not change the Windows startup setting.");
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
        DisabledRules = DefaultRules.Disabled,
        Frequencies = DefaultFrequencies.ToDictionary(),
        KnownAirports = Airports.Select(a => a.Icao).ToList(),
        AirportOverrides = Airports.ToDictionary(a => a.Icao, a => a.ToSettings()),
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
        SettingsNote = IsRunning ? "Changes apply the next time you press Start." : "";
    }

    // ---- connect list ----

    [RelayCommand]
    private void AddConnectAirport()
    {
        var icao = NewConnectAirport.Trim().ToUpperInvariant();
        if (!IsIcao(icao))
        {
            AddLog($"'{NewConnectAirport.Trim()}' is not a 4-letter airport code (for example KSFO).");
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

        var engine = _engineFactory(_settings.ToSyncOptions());
        engine.StatusChanged += (_, s) => OnUi(() => ApplyStatus(s));
        engine.Log += m => OnUi(() => AddLog(m));
        _engine = engine;
        engine.Start();
        SettingsNote = "";
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
            st.LastConfiguredAt is { } t ? t.ToLocalTime().ToString("HH:mm:ss") : "")).ToList();

        Stations.Clear();
        foreach (var r in rows) Stations.Add(r);

        // Airports found in the open vATIS profile join the list (and are remembered for next time).
        var added = false;
        foreach (var icao in rows.Select(r => r.Key[..4].ToUpperInvariant()).Distinct())
        {
            if (Airports.Any(a => a.Icao == icao)) continue;
            InsertAirport(NewAirportPage(icao));
            added = true;
        }
        foreach (var page in Airports)
            page.UpdateStations(rows.Where(r => string.Equals(r.Key[..4], page.Icao, StringComparison.OrdinalIgnoreCase)));
        if (added) Save();
    }

    private static (string Headline, string Detail) Describe(SyncStatus s) => s.Phase switch
    {
        SyncPhase.Stopped => ("Stopped", "Press Start to sync real-world D-ATIS into vATIS."),
        SyncPhase.Starting => ("Starting", s.Message ?? "Getting ready..."),
        SyncPhase.WaitingForVatis => ("Waiting for vATIS", s.Message ?? "vATIS is not reachable. Open vATIS and make sure its websocket is enabled."),
        SyncPhase.Syncing => ("Syncing", s.Message ?? "Updating your ATIS stations..."),
        SyncPhase.Idle => ("Up to date", s.Message ?? "Waiting for the next refresh."),
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
            AddLog($"Could not open {path}: {ex.Message}");
        }
    }

    [RelayCommand]
    private void ClearLog() => LogLines.Clear();

    private void AddLog(string message)
    {
        LogLines.Add($"{DateTime.Now:HH:mm:ss}  {message}");
        while (LogLines.Count > MaxLogLines) LogLines.RemoveAt(0);
    }

    private static void OnUi(Action action)
    {
        var d = Application.Current?.Dispatcher;
        if (d is null || d.CheckAccess()) action();
        else d.BeginInvoke(action);
    }
}
