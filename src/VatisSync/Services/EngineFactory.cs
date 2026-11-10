using VatisSync.Core;

namespace VatisSync.Services;

/// <summary>Wires the real implementations together into a <see cref="SyncEngine"/>.</summary>
public static class EngineFactory
{
    // One HttpClient for the life of the app (recommended; avoids socket exhaustion).
    private static readonly HttpClient Http = CreateHttp();

    public static LocalReplacementStore ReplacementStore { get; } = CreateStore();

    /// <summary>Where the activity log is kept. Set once at startup; without it nothing is written to disk.</summary>
    public static ActivityLog? Log { get; set; }

    private static Action<string>? Trace => Log is null ? null : Log.Trace;

    private static HttpClient CreateHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("VatisSync/0.1 (+https://github.com/hattoriganso/vATIS-Sync)");
        return http;
    }

    private static LocalReplacementStore CreateStore() => new(LocalReplacementStore.DefaultUserFilePath);

    /// <summary>
    /// Asks the running vATIS for the stations of the profile that is open. Read-only: it does not start vATIS
    /// and changes nothing in it. Throws <see cref="VatisUnavailableException"/> if vATIS cannot be reached.
    /// </summary>
    public static Task<IReadOnlyList<VatisStation>> GetProfileStationsAsync(CancellationToken ct) =>
        new VatisClient(new WebSocketVatisSocketFactory(), Trace).GetStationsAsync(ct);

    /// <summary>Builds what an airport's ATIS would be from the real-world D-ATIS, without touching vATIS (the Summary tab).</summary>
    public static DatisPreviewBuilder CreatePreviewBuilder() => new(new DatisFetcher(Http), ReplacementStore);

    public static SyncEngine Create(SyncOptions options, IUpdateApprover? approver = null) => new(
        new VatisClient(new WebSocketVatisSocketFactory(), Trace),
        new DatisFetcher(Http),
        ReplacementStore,
        new CrcPositionResolver(),
        new VatisLauncher(),
        options,
        approver: approver)
    {
        Diagnostics = Trace,
    };
}
