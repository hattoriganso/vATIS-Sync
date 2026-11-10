using System.Text.Json;

namespace VatisSync.Core;

/// <summary>Outcome of a fetch. On failure, Entries holds the last good data (if any) and Error says why.</summary>
public sealed record DatisFetchResult(IReadOnlyList<DatisEntry> Entries, bool FromCache, string? Error)
{
    public bool Succeeded => Error is null;
}

/// <summary>Downloads https://atis.info/api/all and keeps the last good copy.</summary>
public sealed class DatisFetcher : IDatisSource
{
    public static readonly Uri DefaultUrl = new("https://atis.info/api/all");

    /// <summary>The whole list is well under 100 KB; anything past this is not one, and is not read into memory.</summary>
    public const int MaxResponseBytes = 5_000_000;

    /// <summary>A real ATIS is a couple of thousand characters at most. A longer one is left out, as if the airport had none.</summary>
    public const int MaxDatisChars = 10_000;

    private static readonly TimeSpan BodyTimeout = TimeSpan.FromSeconds(30);

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
            // Headers first, so the body can be read with a limit on its size, and a limit of our own on the time:
            // the client's timeout stops counting once the headers are in.
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(BodyTimeout);
            using var response = await _http.GetAsync(_url, HttpCompletionOption.ResponseHeadersRead, limit.Token)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return Failed($"atis.info returned HTTP {(int)response.StatusCode}");

            var body = await ReadLimitedAsync(response.Content, limit.Token).ConfigureAwait(false);
            if (body is null) return Failed($"atis.info sent more than {MaxResponseBytes / 1_000_000} MB, which is not an ATIS list");
            var entries = Parse(body, out var error);
            if (entries is null) return Failed(error!);

            _lastGood = entries;
            return new DatisFetchResult(entries, FromCache: false, Error: null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return Failed("Timed out contacting atis.info");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            return Failed($"Could not reach atis.info: {ex.Message}");
        }
    }

    /// <summary>The body as text, or null if it is larger than <see cref="MaxResponseBytes"/>.</summary>
    private static async Task<string?> ReadLimitedAsync(HttpContent content, CancellationToken ct)
    {
        if (content.Headers.ContentLength > MaxResponseBytes) return null;

        await using var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > MaxResponseBytes) return null;
            buffer.Write(chunk, 0, read);
        }

        buffer.Position = 0;
        using var reader = new StreamReader(buffer, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return await reader.ReadToEndAsync(ct).ConfigureAwait(false);
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
                var datis = Str(item, "datis") ?? "";
                if (datis.Length > MaxDatisChars) continue;
                list.Add(new DatisEntry(airport, type, datis, Str(item, "updatedAt")));
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
