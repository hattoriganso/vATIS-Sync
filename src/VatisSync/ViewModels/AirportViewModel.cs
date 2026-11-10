using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using VatisSync.Core;

namespace VatisSync.ViewModels;

/// <param name="Connected">The station is connected to the network (the ATIS is live).</param>
/// <param name="Synced">The real-world D-ATIS text has been written into the station.</param>
/// <param name="Desynced">A change the controller disregarded is still waiting, so vATIS differs from the real world.</param>
public sealed record StationRow(
    string Key, string Connection, string Datis, string LastUpdated, bool Connected = false, bool Synced = false,
    bool Desynced = false, bool NoDatis = false)
{
    /// <summary>The D-ATIS column says something the controller should notice.</summary>
    public bool NeedsAttention => Desynced || NoDatis;
}

public enum AirportStatus
{
    /// <summary>In the profile, nothing written yet.</summary>
    Ready,

    /// <summary>The D-ATIS text has been written, but the ATIS is not connected.</summary>
    Synced,

    /// <summary>At least one station is connected: the ATIS is live.</summary>
    Started,

    /// <summary>No longer in the vATIS profile.</summary>
    Stale,

    /// <summary>The controller disregarded a change, so vATIS differs from the real-world ATIS.</summary>
    Desynced,

    /// <summary>A station is connected, but there is no current real-world D-ATIS to put in it.</summary>
    NoDatis,
}

/// <summary>The marker for the pinned first entry of the airport list: overall status and the log.</summary>
public sealed class OverviewPage
{
    public string Title => "Overview";
}

/// <summary>One airport's page: its stations, and its rules and frequencies (the defaults, or its own copy).</summary>
public sealed partial class AirportViewModel : ObservableObject
{
    private readonly Func<List<string>> _defaultRules;
    private readonly Func<Dictionary<FrequencyRole, FrequencySetting>> _defaultFrequencies;
    private readonly Action _changed;

    public AirportViewModel(string icao, AirportSettings? own,
        Func<List<string>> defaultRules, Func<Dictionary<FrequencyRole, FrequencySetting>> defaultFrequencies,
        Action changed,
        Func<IReadOnlyList<string>, AirportRules, bool, CancellationToken, Task<PreviewResult>>? previewBuilder = null)
    {
        Icao = icao;
        _defaultRules = defaultRules;
        _defaultFrequencies = defaultFrequencies;
        _changed = changed;

        _useDefaultRules = own?.UseDefaultRules ?? true;
        _useDefaultFrequencies = own?.UseDefaultFrequencies ?? true;
        Rules = new RuleSetEditor(_useDefaultRules ? defaultRules() : own!.DisabledRules, changed);
        Frequencies = new FrequencySetEditor(_useDefaultFrequencies ? defaultFrequencies() : own!.Frequencies, changed);

        Summary = new AirportSummary(
            () => StationKeys, EffectiveRules,
            previewBuilder ?? ((_, _, _, _) => Task.FromResult(new PreviewResult([], null, false, default))));
    }

    /// <summary>The Summary tab: the D-ATIS text, how it differs from the real one, and where each station stands.</summary>
    public AirportSummary Summary { get; }

    /// <summary>Which tab is showing: 0 is Summary (the one people see first), 1 is Settings.</summary>
    [ObservableProperty] private int _selectedTab;

    /// <summary>This airport's stations as the vATIS profile lists them (KMIA_A, KMIA_D), set whenever the profile is read.</summary>
    public IReadOnlyList<string> ProfileStations { get; set; } = [];

    /// <summary>The stations the Summary is about: the profile's, else the ones the sync has seen. None once it is stale.</summary>
    public IReadOnlyList<string> StationKeys =>
        IsStale ? []
        : ProfileStations.Count > 0 ? ProfileStations
        : Stations.Select(s => s.Key).ToList();

    /// <summary>This airport's rules and frequency settings with the defaults filled in, as the sync would use them.</summary>
    public AirportRules EffectiveRules()
    {
        var own = ToSettings();
        return new AirportRules(
            own.UseDefaultRules ? _defaultRules() : own.DisabledRules,
            (own.UseDefaultFrequencies ? _defaultFrequencies() : own.Frequencies)
                .ToDictionary(kv => kv.Key, kv => FrequencyDetector.Usable(kv.Value)));
    }

    public string Icao { get; }
    public RuleSetEditor Rules { get; }
    public FrequencySetEditor Frequencies { get; }

    public ObservableCollection<StationRow> Stations { get; } = [];
    public bool HasNoStations => Stations.Count == 0;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(Status), nameof(StatusText))]
    private bool _isConnected;

    /// <summary>The real-world text has been written into at least one of its stations.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Status), nameof(StatusText))]
    private bool _isSynced;

    /// <summary>vATIS was read and this airport is not in the profile any more. It stays listed so its settings are not lost.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Status), nameof(StatusText), nameof(NeedsAttention))]
    private bool _isStale;

    /// <summary>A change the controller disregarded is waiting for at least one station.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Status), nameof(StatusText), nameof(NeedsAttention))]
    private bool _isDesynced;

    /// <summary>A connected station has no current real-world D-ATIS.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Status), nameof(StatusText), nameof(NeedsAttention))]
    private bool _hasNoDatis;

    /// <summary>Its dot in the list is amber, not green: live, but not what the real world says.</summary>
    public bool NeedsAttention => !IsStale && (IsDesynced || HasNoDatis);

    /// <summary>Stale outranks everything (nothing is synced for it); then desynced and no D-ATIS (they must not hide behind Started); then live, then written, then waiting.</summary>
    public AirportStatus Status => IsStale ? AirportStatus.Stale
        : IsDesynced ? AirportStatus.Desynced
        : HasNoDatis ? AirportStatus.NoDatis
        : IsConnected ? AirportStatus.Started
        : IsSynced ? AirportStatus.Synced
        : AirportStatus.Ready;

    public string StatusText => Messages.Get(Status switch
    {
        AirportStatus.Stale => Msg.AirportStale,
        AirportStatus.Desynced => Msg.AirportDesynced,
        AirportStatus.NoDatis => Msg.AirportNoDatis,
        AirportStatus.Started => Msg.AirportStarted,
        AirportStatus.Synced => Msg.AirportSynced,
        _ => Msg.AirportReady,
    });

    [ObservableProperty, NotifyPropertyChangedFor(nameof(CustomRules))]
    private bool _useDefaultRules;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(CustomFrequencies))]
    private bool _useDefaultFrequencies;

    public bool CustomRules => !UseDefaultRules;
    public bool CustomFrequencies => !UseDefaultFrequencies;

    // Unticking "use the default settings" starts the airport's own copy from the defaults as they are now.
    partial void OnUseDefaultRulesChanged(bool value)
    {
        if (!value) Rules.Load(_defaultRules());
        _changed();
    }

    partial void OnUseDefaultFrequenciesChanged(bool value)
    {
        if (!value) Frequencies.Load(_defaultFrequencies());
        _changed();
    }

    public AirportSettings ToSettings() => new()
    {
        UseDefaultRules = UseDefaultRules,
        DisabledRules = Rules.Disabled,
        UseDefaultFrequencies = UseDefaultFrequencies,
        Frequencies = Frequencies.ToDictionary(),
    };

    public void UpdateStations(IEnumerable<StationRow> rows)
    {
        var list = rows.ToList();
        Stations.Clear();
        foreach (var r in list) Stations.Add(r);
        Summary.SetEngineRows(list);
        IsConnected = Stations.Any(s => s.Connected);
        IsSynced = Stations.Any(s => s.Synced);
        IsDesynced = Stations.Any(s => s.Desynced);
        HasNoDatis = Stations.Any(s => s.NoDatis);
        OnPropertyChanged(nameof(HasNoStations));
    }
}
