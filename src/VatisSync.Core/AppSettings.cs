using System.Text.Json;

namespace VatisSync.Core;

/// <summary>User preferences, stored as JSON in %APPDATA%\VatisSync\settings.json.</summary>
public sealed record AppSettings
{
    public const int MinIntervalMinutes = 1;
    public const int MaxIntervalMinutes = 60;
    public const int MaxConnectionsLimit = 4;

    /// <summary>Minutes between refreshes.</summary>
    public int IntervalMinutes { get; init; } = 5;

    /// <summary>Simultaneous ATIS connections to allow (vATIS itself allows at most 4).</summary>
    public int MaxConnections { get; init; } = MaxConnectionsLimit;

    /// <summary>Airports (ICAO) to connect when syncing. Empty follows the CRC position, if enabled.</summary>
    public List<string> Airports { get; init; } = [];

    public bool AutoConnectActivePosition { get; init; } = true;

    /// <summary>With no airports chosen and no CRC position to follow, connect every D-ATIS station in the open vATIS profile.</summary>
    public bool ConnectProfileStations { get; init; } = true;
    public bool LaunchVatis { get; init; } = true;

    /// <summary>When to ask the controller before a change reaches a connected ATIS.</summary>
    public ApprovalMode Approval { get; init; } = ApprovalMode.RunwayChanges;

    /// <summary>Built-in rule ids the user switched off.</summary>
    public List<string> DisabledRules { get; init; } = [];

    /// <summary>Default per-position handling of frequencies. Roles not listed are Off. Airports may customize their own.</summary>
    public Dictionary<FrequencyRole, FrequencySetting> Frequencies { get; init; } = [];

    /// <summary>Airports (ICAO) the app has seen in the open vATIS profile or the user added by hand; shown in the airport list.</summary>
    public List<string> KnownAirports { get; init; } = [];

    /// <summary>Airports (ICAO) that customize rules and/or frequencies instead of following the defaults.</summary>
    public Dictionary<string, AirportSettings> AirportOverrides { get; init; } = [];

    /// <summary>Start syncing as soon as the app opens.</summary>
    public bool StartSyncOnLaunch { get; init; }

    /// <summary>Start the app when Windows starts.</summary>
    public bool StartWithWindows { get; init; }

    /// <summary>Open minimized to the tray instead of showing the window.</summary>
    public bool StartMinimized { get; init; }

    /// <summary>Closing the window keeps the app running in the tray.</summary>
    public bool CloseToTray { get; init; } = true;

    /// <summary>The "still running in the tray" notification has been shown once already.</summary>
    public bool TrayHintShown { get; init; }

    /// <summary>Out-of-range or malformed values replaced with safe ones.</summary>
    public AppSettings Normalized() => this with
    {
        IntervalMinutes = Math.Clamp(IntervalMinutes, MinIntervalMinutes, MaxIntervalMinutes),
        MaxConnections = Math.Clamp(MaxConnections, 1, MaxConnectionsLimit),
        Airports = CleanIcaos(Airports),
        DisabledRules = (DisabledRules ?? []).Where(r => !string.IsNullOrWhiteSpace(r)).Distinct().ToList(),
        Frequencies = CleanFrequencies(Frequencies),
        KnownAirports = CleanIcaos(KnownAirports),
        AirportOverrides = CleanOverrides(AirportOverrides),
    };

    public SyncOptions ToSyncOptions()
    {
        var n = Normalized();
        return new SyncOptions
        {
            Interval = TimeSpan.FromMinutes(n.IntervalMinutes),
            MaxConnections = n.MaxConnections,
            Airports = n.Airports,
            AutoConnectActivePosition = n.AutoConnectActivePosition,
            ConnectProfileStations = n.ConnectProfileStations,
            LaunchVatis = n.LaunchVatis,
            DisabledRules = n.DisabledRules,
            Approval = n.Approval,
            Frequencies = n.Frequencies.ToDictionary(kv => kv.Key, kv => FrequencyDetector.Usable(kv.Value)),
            PerAirport = n.AirportOverrides.ToDictionary(kv => kv.Key, kv => kv.Value.Resolve(n)),
        };
    }

    /// <summary>Drops roles that are Off. An Override with no valid frequency yet is kept (the user may still be typing);
    /// <see cref="ToSyncOptions"/> treats it as Off.</summary>
    internal static Dictionary<FrequencyRole, FrequencySetting> CleanFrequencies(
        Dictionary<FrequencyRole, FrequencySetting>? frequencies) =>
        (frequencies ?? [])
            .Where(kv => kv.Value is not null)
            .Where(kv => kv.Value.Mode != FrequencyMode.Off)
            .ToDictionary(kv => kv.Key, kv => kv.Value with { Frequency = kv.Value.Frequency?.Trim() });

    /// <summary>ICAO keys upper-cased; entries that follow the defaults everywhere are dropped.</summary>
    private static Dictionary<string, AirportSettings> CleanOverrides(Dictionary<string, AirportSettings>? overrides)
    {
        var result = new Dictionary<string, AirportSettings>();
        foreach (var (key, value) in overrides ?? [])
        {
            var icao = key?.Trim().ToUpperInvariant() ?? "";
            if (icao.Length != 4 || !icao.All(char.IsLetterOrDigit) || value is null) continue;
            var cleaned = value.Cleaned();
            if (!cleaned.IsDefault) result[icao] = cleaned;
        }
        return result;
    }

    private static List<string> CleanIcaos(IEnumerable<string>? airports) =>
        (airports ?? []).Select(a => a?.Trim().ToUpperInvariant() ?? "")
            .Where(a => a.Length == 4 && a.All(char.IsLetterOrDigit))
            .Distinct().ToList();
}

/// <summary>Reads and writes <see cref="AppSettings"/>. Never throws: a bad file gives defaults and a warning.</summary>
public sealed class SettingsStore(string? path = null)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true,
    };

    public string FilePath { get; } = path ?? DefaultPath;

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VatisSync", "settings.json");

    /// <summary>Raised when the file exists but cannot be used, or cannot be written.</summary>
    public event Action<string>? Warning;

    public AppSettings Load()
    {
        if (!File.Exists(FilePath)) return new AppSettings();
        try
        {
            return (JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Json) ?? new AppSettings()).Normalized();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            Warning?.Invoke(Messages.Get(Msg.SettingsReadFailed, new { path = FilePath, reason = ex.Message }));
            return new AppSettings();
        }
    }

    /// <returns>False if the file could not be written.</returns>
    public bool Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(settings.Normalized(), Json));
            File.Move(temp, FilePath, overwrite: true); // never leaves a half-written settings file
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Warning?.Invoke(Messages.Get(Msg.SettingsSaveFailed, new { path = FilePath, reason = ex.Message }));
            return false;
        }
    }
}
