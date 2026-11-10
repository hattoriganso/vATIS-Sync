using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VatisSync.Core;

namespace VatisSync.ViewModels;

/// <summary>One station's card on the Summary tab.</summary>
public sealed partial class StationSummary : ObservableObject
{
    public StationSummary(StationPreview preview, DateTimeOffset now, string statusLine, bool showChanges)
    {
        Key = preview.Station;
        Available = preview.Available;
        Problem = preview.Problem ?? "";
        SourceText = PreviewText.Source(preview, now) ?? "";
        ConditionsDiff = preview.ConditionsDiff;
        NotamsDiff = preview.NotamsDiff;
        StatusLine = statusLine;
        _showChanges = showChanges;
    }

    public string Key { get; }
    public bool Available { get; }
    public bool HasProblem => !Available;
    public string Problem { get; }
    public string SourceText { get; }
    public IReadOnlyList<DiffSegment> ConditionsDiff { get; }
    public IReadOnlyList<DiffSegment> NotamsDiff { get; }

    /// <summary>Where the station stands in vATIS (live, written, waiting). Updated while the sync runs.</summary>
    [ObservableProperty] private string _statusLine;

    [ObservableProperty] private bool _showChanges;
}

/// <summary>
/// The Summary tab of an airport: the D-ATIS text for each of its stations, how it differs from the real-world text, how old
/// it is, and where each station stands. It works before the sync is started by building the text itself (read-only).
/// </summary>
public sealed partial class AirportSummary : ObservableObject
{
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(400);

    private readonly Func<IReadOnlyList<string>> _stationKeys;
    private readonly Func<AirportRules> _rules;
    private readonly Func<IReadOnlyList<string>, AirportRules, bool, CancellationToken, Task<PreviewResult>> _build;
    private readonly SynchronizationContext? _ui = SynchronizationContext.Current;
    private readonly Func<DateTimeOffset> _now;

    private IReadOnlyList<StationRow> _engineRows = [];
    private CancellationTokenSource? _pending;
    private int _version;
    private bool _dirty = true;
    private DateTimeOffset? _builtAt;

    public AirportSummary(
        Func<IReadOnlyList<string>> stationKeys,
        Func<AirportRules> rules,
        Func<IReadOnlyList<string>, AirportRules, bool, CancellationToken, Task<PreviewResult>> build,
        Func<DateTimeOffset>? now = null)
    {
        _stationKeys = stationKeys;
        _rules = rules;
        _build = build;
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    public ObservableCollection<StationSummary> Stations { get; } = [];

    [ObservableProperty] private bool _isLoading;

    /// <summary>A line instead of the stations: loading, or nothing to show.</summary>
    [ObservableProperty] private string _message = "";

    /// <summary>A yellow note: the real-world data could not be downloaded.</summary>
    [ObservableProperty] private string _warning = "";

    [ObservableProperty] private bool _showChanges;

    public string Note => Messages.Get(Msg.SummaryNote);

    partial void OnShowChangesChanged(bool value)
    {
        foreach (var s in Stations) s.ShowChanges = value;
    }

    /// <summary>Something the text depends on changed (settings, or which stations the airport has).</summary>
    public void MarkDirty() => _dirty = true;

    /// <summary>Builds the summary if it has never been built, something changed, or the data is a minute old. Otherwise does nothing.</summary>
    public void EnsureFresh()
    {
        var stale = _builtAt is null || _now() - _builtAt > DatisPreviewBuilder.CacheFor;
        if (_dirty || stale) _ = RefreshAsync(force: false);
    }

    /// <summary>Rebuilds shortly from now. Repeated calls (typing a frequency) wait for the last one.</summary>
    public void ScheduleRefresh()
    {
        _dirty = true;
        _pending?.Cancel();
        var cts = _pending = new CancellationTokenSource();
        _ = Task.Delay(Debounce, cts.Token).ContinueWith(t =>
        {
            if (t.IsCanceled) return;
            if (_ui is not null) _ui.Post(_ => _ = RefreshAsync(force: false), null);
            else _ = RefreshAsync(force: false);
        }, TaskScheduler.Default);
    }

    /// <summary>The engine's view of each station, to show where each one stands.</summary>
    public void SetEngineRows(IEnumerable<StationRow> rows)
    {
        _engineRows = rows.ToList();
        foreach (var s in Stations) s.StatusLine = StatusFor(s.Key);
    }

    [RelayCommand]
    private Task UpdateNow() => RefreshAsync(force: true);

    public async Task RefreshAsync(bool force)
    {
        var keys = _stationKeys();
        if (keys.Count == 0)
        {
            Stations.Clear();
            Warning = "";
            Message = Messages.Get(Msg.SummaryNoStations);
            _dirty = false;
            _builtAt = _now();
            return;
        }

        var mine = ++_version;
        IsLoading = true;
        if (Stations.Count == 0) Message = Messages.Get(Msg.SummaryLoading);
        try
        {
            var result = await _build(keys, _rules(), force, CancellationToken.None);
            if (mine != _version) return; // a newer request is on its way; let it have the last word

            var now = _now();
            Stations.Clear();
            foreach (var p in result.Stations) Stations.Add(new StationSummary(p, now, StatusFor(p.Station), ShowChanges));
            Message = "";
            Warning = result.FetchProblem is null ? ""
                : Messages.Get(result.Stations.Any(s => s.Available) ? Msg.SummaryFetchFailedCached : Msg.SummaryFetchFailed,
                    new { reason = result.FetchProblem });
            _dirty = false;
            _builtAt = now;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (mine != _version) return;
            Warning = Messages.Get(Msg.SummaryFetchFailed, new { reason = ex.Message });
            Message = "";
        }
        finally
        {
            if (mine == _version) IsLoading = false;
        }
    }

    private string StatusFor(string key)
    {
        var row = _engineRows.FirstOrDefault(r => string.Equals(r.Key, key, StringComparison.OrdinalIgnoreCase));
        return row switch
        {
            { Connected: true } => Messages.Get(Msg.SummaryStatusConnected),
            { Synced: true } => Messages.Get(Msg.SummaryStatusSynced, new { time = row.LastUpdated }),
            _ => Messages.Get(Msg.SummaryStatusReady),
        };
    }
}
