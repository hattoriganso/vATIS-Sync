using VatisSync.Core;

namespace VatisSync.Services;

/// <summary>Wires the real implementations together into a <see cref="SyncEngine"/>.</summary>
public static class EngineFactory
{
    // One HttpClient for the life of the app (recommended; avoids socket exhaustion).
    private static readonly HttpClient Http = CreateHttp();

    public static LocalReplacementStore ReplacementStore { get; } = CreateStore();

    private static HttpClient CreateHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("VatisSync/0.1 (+https://github.com/hattoriganso/vATIS-Sync)");
        return http;
    }

    private static LocalReplacementStore CreateStore() => new(LocalReplacementStore.DefaultUserFilePath);

    public static SyncEngine Create(SyncOptions options, IUpdateApprover? approver = null) => new(
        new VatisClient(new WebSocketVatisSocketFactory()),
        new DatisFetcher(Http),
        ReplacementStore,
        new CrcPositionResolver(),
        new VatisLauncher(),
        options,
        approver: approver);
}
