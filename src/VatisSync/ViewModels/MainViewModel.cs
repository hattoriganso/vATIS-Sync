using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VatisSync.Core;
using VatisSync.Services;

namespace VatisSync.ViewModels;

public sealed record ApprovalChoice(ApprovalMode Mode, string Label);

public sealed record StationRow(string Key, string Connection, string Datis, string LastUpdated);

public sealed partial class RuleToggle(string id, string name, string description, bool enabled, Action<RuleToggle> changed)
    : ObservableObject
{
    public string Id { get; } = id;
    public string Name { get; } = name;
    public string Description { get; } = description;

    [ObservableProperty] private bool _enabled = enabled;

    partial void OnEnabledChanged(bool value) => changed(this);
}

public sealed record FrequencyModeChoice(FrequencyMode Mode, string Label);

/// <summary>One position's frequency handling in the Rules tab: keep, remove, or replace with a frequency the user types.</summary>
public sealed partial class FrequencyRoleSetting : ObservableObject
{
    private readonly Action<FrequencyRoleSetting> _changed;

    public FrequencyRoleSetting(FrequencyRole role, string name, string example, FrequencySetting? setting,
        Action<FrequencyRoleSetting> changed)
    {
        Role = role;
        Name = name;
        Example = example;
        _mode = setting?.Mode ?? FrequencyMode.Off;
        _frequency = setting?.Frequency ?? "";
        _changed = changed;
    }

    public FrequencyRole Role { get; }
    public string Name { get; }
    public string Example { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOverride), nameof(FrequencyProblem))]
    private FrequencyMode _mode;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FrequencyProblem))]
    private string _frequency;

    public bool IsOverride => Mode == FrequencyMode.Override;

    /// <summary>Shown beside the box while the frequency is missing or not a valid one (such as 121.900).</summary>
    public string FrequencyProblem => IsOverride && !FrequencyDetector.IsValidFrequency(Frequency?.Trim())
        ? "Type a frequency such as 121.900. Until then the real text is kept."
        : "";

    public FrequencySetting ToSetting() => new(Mode, Mode == FrequencyMode.Override ? Frequency.Trim() : null);

    partial void OnModeChanged(FrequencyMode value) => _changed(this);
    partial void OnFrequencyChanged(string value) => _changed(this);
}

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

    public static IReadOnlyList<FrequencyModeChoice> FrequencyModeChoices { get; } =
    [
        new(FrequencyMode.Off, "Keep as is"),
        new(FrequencyMode.Remove, "Remove"),
        new(FrequencyMode.Override, "Replace with"),
    ];

    private static readonly (FrequencyRole Role, string Name, string Example)[] FrequencyRoles =
    [
        (FrequencyRole.ClearanceDelivery, "Clearance delivery", "CTC CD ON 121.7"),
        (FrequencyRole.Ground, "Ground", "GROUND CONTROL ON 121.9"),
        (FrequencyRole.Tower, "Tower", "TWR FREQ 118.5"),
        (FrequencyRole.Departure, "Departure", "DEPT FREQ IS 119.35"),
        (FrequencyRole.Approach, "Approach", "ALL ARRS CTC APCH 124.0"),
        (FrequencyRole.Ramp, "Ramp", "RAMP CONTROL OPEN 126.225"),
        (FrequencyRole.Metering, "Metering", "CONTACT GND METERING ON 121.67"),
        (FrequencyRole.Helicopter, "Helicopter control", "HELICOPTOR CONTROL OPEN ON 119.9"),
        (FrequencyRole.Other, "Other (no position named)", "RWY 9 FREQ 123.9"),
    ];

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

        foreach (var a in _settings.Airports) Airports.Add(a);
        foreach (var r in BuiltInRules.All)
            Rules.Add(new RuleToggle(r.Id, r.Name, r.Description,
                !_settings.DisabledRules.Contains(r.Id, StringComparer.OrdinalIgnoreCase), _ => Save()));

        foreach (var (role, name, example) in FrequencyRoles)
            Frequencies.Add(new FrequencyRoleSetting(role, name, example,
                _settings.Frequencies.GetValueOrDefault(role), _ => Save()));

        _loading = false;
        ApplyStatus(SyncStatus.Initial);
    }

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
    [ObservableProperty] private string _newAirport = "";
    [ObservableProperty] private string? _selectedAirport;

    public ObservableCollection<string> Airports { get; } = [];
    public ObservableCollection<RuleToggle> Rules { get; } = [];
    public ObservableCollection<FrequencyRoleSetting> Frequencies { get; } = [];

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
        Airports = [.. Airports],
        AutoConnectActivePosition = AutoConnectActivePosition,
        ConnectProfileStations = ConnectProfileStations,
        LaunchVatis = LaunchVatis,
        Approval = Approval,
        DisabledRules = Rules.Where(r => !r.Enabled).Select(r => r.Id).ToList(),
        Frequencies = Frequencies.Where(f => f.Mode != FrequencyMode.Off).ToDictionary(f => f.Role, f => f.ToSetting()),
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

    // ---- airports ----

    [RelayCommand]
    private void AddAirport()
    {
        var icao = NewAirport.Trim().ToUpperInvariant();
        if (icao.Length != 4 || !icao.All(char.IsLetterOrDigit))
        {
            AddLog($"'{NewAirport.Trim()}' is not a 4-letter airport code (for example KSFO).");
            return;
        }
        if (!Airports.Contains(icao)) { Airports.Add(icao); Save(); }
        NewAirport = "";
    }

    [RelayCommand]
    private void RemoveAirport()
    {
        if (SelectedAirport is null) return;
        Airports.Remove(SelectedAirport);
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

        Stations.Clear();
        foreach (var st in s.Stations)
            Stations.Add(new StationRow(
                st.Key,
                st.Connected ? "Connected" : "Not connected",
                !st.Connected ? "" : st.DatisAvailable ? "D-ATIS loaded" : "No current D-ATIS",
                st.LastConfiguredAt is { } t ? t.ToLocalTime().ToString("HH:mm:ss") : ""));
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
