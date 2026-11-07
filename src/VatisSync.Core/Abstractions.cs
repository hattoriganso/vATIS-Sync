namespace VatisSync.Core;

public interface IVatisClient
{
    Task<bool> WaitUntilReadyAsync(TimeSpan limit, CancellationToken ct = default);
    Task<IReadOnlyList<VatisStation>> GetStationsAsync(CancellationToken ct = default);
    Task<VatisAtisState> GetAtisAsync(string stationId, CancellationToken ct = default);
    Task<IReadOnlyList<KeyValuePair<string, string>>> GetContractionsAsync(string stationKey, CancellationToken ct = default);
    Task ConfigureAtisAsync(string stationId, string airportConditions, string notams, bool syncAtisLetter, CancellationToken ct = default);
    Task<bool> ConnectAtisAsync(string stationId, CancellationToken ct = default);
    Task DisconnectAtisAsync(string stationId, CancellationToken ct = default);
}

public interface IDatisSource
{
    Task<DatisFetchResult> FetchAsync(CancellationToken ct = default);
}

public interface IReplacementSource
{
    /// <summary>Rules per airport ICAO (e.g. "KSFO"). Airports without rules are absent from the result.</summary>
    Task<IReadOnlyDictionary<string, ReplacementRules>> GetAsync(IEnumerable<string> airports, CancellationToken ct = default);
}

/// <summary>The position last used in CRC, split from its callsign: "SFO_TWR" -> ("SFO", "TWR").</summary>
public sealed record CrcPosition(string Prefix, string Suffix);

public interface IActivePositionProvider
{
    CrcPosition? GetActivePosition();
}

public enum LaunchResult { AlreadyRunning, Started, NotFound }

public interface IVatisLauncher
{
    LaunchResult EnsureRunning();
}
