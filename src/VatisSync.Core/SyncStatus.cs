namespace VatisSync.Core;

public sealed record SyncOptions
{
    /// <summary>Time between refreshes of connected stations.</summary>
    public TimeSpan Interval { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>vATIS allows only so many simultaneous ATIS connections; never exceed this.</summary>
    public int MaxConnections { get; init; } = 4;

    /// <summary>
    /// Airports (ICAO) to connect. Empty means: follow the position last used in CRC if it is a
    /// TWR/GND/DEL/RMP position (and <see cref="AutoConnectActivePosition"/> is on), otherwise connect every
    /// D-ATIS station of the open vATIS profile (if <see cref="ConnectProfileStations"/> is on) or nothing.
    /// Text is written to, and refreshed for, every D-ATIS station of the profile whatever is connected.
    /// </summary>
    public IReadOnlyList<string> Airports { get; init; } = [];

    public bool AutoConnectActivePosition { get; init; } = true;

    /// <summary>With no airports chosen and no position to follow, connect the profile's D-ATIS stations (up to <see cref="MaxConnections"/>).</summary>
    public bool ConnectProfileStations { get; init; } = true;

    public bool LaunchVatis { get; init; } = true;

    /// <summary>When the controller must approve changes to a connected ATIS.</summary>
    public ApprovalMode Approval { get; init; } = ApprovalMode.RunwayChanges;

    /// <summary>Ids of built-in cleanup rules (see <see cref="BuiltInRules"/>) the user switched off.</summary>
    public IReadOnlyList<string> DisabledRules { get; init; } = [];

    /// <summary>What to do with the frequencies of each position (see <see cref="FrequencyDetector"/>). Missing roles are Off.</summary>
    public IReadOnlyDictionary<FrequencyRole, FrequencySetting> Frequencies { get; init; } =
        new Dictionary<FrequencyRole, FrequencySetting>();

    /// <summary>How long to wait for vATIS to answer after launching it, per attempt.</summary>
    public TimeSpan StartupTimeout { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Pause before trying again after vATIS becomes unreachable.</summary>
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Pause after connecting, before trimming connections over the limit.</summary>
    public TimeSpan PostConnectDelay { get; init; } = TimeSpan.FromSeconds(5);
}

public enum SyncPhase
{
    Stopped,
    Starting,
    WaitingForVatis,
    Syncing,
    Idle,
}

public sealed record StationStatus(
    string Key,
    bool Connected,
    bool DatisAvailable,
    DateTimeOffset? LastConfiguredAt);

public sealed record SyncStatus(
    SyncPhase Phase,
    string? Message,
    DateTimeOffset? LastSync,
    DateTimeOffset? NextSync,
    IReadOnlyList<StationStatus> Stations)
{
    public static SyncStatus Initial { get; } = new(SyncPhase.Stopped, null, null, null, []);
}
