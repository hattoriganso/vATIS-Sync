using System.Text.Json;

namespace VatisSync.Core;

/// <summary>Outcome of a fetch. On failure, Entries holds the last good data (if any) and Error says why.</summary>
public sealed record DatisFetchResult(IReadOnlyList<DatisEntry> Entries, bool FromCache, string? Error)
{
    public bool Succeeded => Error is null;
}

/// <summary>Downloads https://atis.info/api/all and keeps the last good copy.</summary>
public sealed class DatisFetcher
{
    public static readonly Uri DefaultUrl = new("https://atis.info/api/all");

    private readonly HttpClient _http;
    private readonly Uri _url;
    private IReadOnlyList<DatisEntry> _lastGood = [];

    public DatisFetcher(HttpClient http, Uri? url = null)
    {
        _http = http;
        _url = url ?? DefaultUrl;
    }

    public async Task<DatisFetchResult> FetchAsync(CancellationToken ct = default)
    {
        try
        {
            using var response = await _http.GetAsync(_url, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return Failed($"atis.info returned HTTP {(int)response.StatusCode}");

            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var entries = Parse(body, out var error);
            if (entries is null) return Failed(error!);

            _lastGood = entries;
            return new DatisFetchResult(entries, FromCache: false, Error: null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException)
        {
            return Failed("Timed out contacting atis.info");
        }
        catch (HttpRequestException ex)
        {
            return Failed($"Could not reach atis.info: {ex.Message}");
        }
    }

    private DatisFetchResult Failed(string error) =>
        new(_lastGood, FromCache: _lastGood.Count > 0, Error: error);

    /// <summary>Parses the API body. Returns null (with an error) for anything that isn't an array of entries.</summary>
    public static List<DatisEntry>? Parse(string json, out string? error)
    {
        error = null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                // The API reports problems as {"error": "..."}.
                error = doc.RootElement.ValueKind == JsonValueKind.Object &&
                        doc.RootElement.TryGetProperty("error", out var e)
                    ? $"atis.info error: {e}"
                    : "Unexpected response from atis.info";
                return null;
            }

            var list = new List<DatisEntry>();
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var airport = Str(item, "airport");
                var type = Str(item, "type");
                if (airport is null || type is null) continue;
                list.Add(new DatisEntry(airport, type, Str(item, "datis") ?? "", Str(item, "updatedAt")));
            }
            return list;
        }
        catch (JsonException ex)
        {
            error = $"Could not read atis.info response: {ex.Message}";
            return null;
        }
    }

    private static string? Str(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
