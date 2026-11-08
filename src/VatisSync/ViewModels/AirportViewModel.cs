using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using VatisSync.Core;

namespace VatisSync.ViewModels;

/// <param name="Connected">The station is connected to the network (the ATIS is live).</param>
/// <param name="Synced">The real-world D-ATIS text has been written into the station.</param>
public sealed record StationRow(
    string Key, string Connection, string Datis, string LastUpdated, bool Connected = false, bool Synced = false);

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
        Action changed)
    {
        Icao = icao;
        _defaultRules = defaultRules;
        _defaultFrequencies = defaultFrequencies;
        _changed = changed;

        _useDefaultRules = own?.UseDefaultRules ?? true;
        _useDefaultFrequencies = own?.UseDefaultFrequencies ?? true;
        Rules = new RuleSetEditor(_useDefaultRules ? defaultRules() : own!.DisabledRules, changed);
        Frequencies = new FrequencySetEditor(_useDefaultFrequencies ? defaultFrequencies() : own!.Frequencies, changed);
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
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Status), nameof(StatusText))]
    private bool _isStale;

    /// <summary>Stale outranks everything (nothing is synced for it); then live, then written, then waiting.</summary>
    public AirportStatus Status => IsStale ? AirportStatus.Stale
        : IsConnected ? AirportStatus.Started
        : IsSynced ? AirportStatus.Synced
        : AirportStatus.Ready;

    public string StatusText => Messages.Get(Status switch
    {
        AirportStatus.Stale => Msg.AirportStale,
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
        Stations.Clear();
        foreach (var r in rows) Stations.Add(r);
        IsConnected = Stations.Any(s => s.Connected);
        IsSynced = Stations.Any(s => s.Synced);
        OnPropertyChanged(nameof(HasNoStations));
    }
}
