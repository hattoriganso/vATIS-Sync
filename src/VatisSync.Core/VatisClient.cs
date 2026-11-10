using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace VatisSync.Core;

public sealed class VatisUnavailableException(string message, Exception? inner = null)
    : Exception(message, inner);

public sealed record VatisStation(string Id, string Name, string AtisType, IReadOnlyList<string> Presets)
{
    /// <summary>Name as used by the sync logic: "KSFO", "KSFO_A" (arrival) or "KSFO_D" (departure).</summary>
    public string Key => AtisType switch
    {
        "Arrival" => Name + "_A",
        "Departure" => Name + "_D",
        _ => Name,
    };

    public bool HasDatisPreset => Presets.Contains("D-ATIS");
}

public sealed record VatisAtisState(string NetworkConnectionStatus, string AirportConditions, string Notams)
{
    public bool IsConnected => NetworkConnectionStatus == "Connected";
    public bool IsDisconnected => NetworkConnectionStatus == "Disconnected";
}

/// <summary>
/// Talks to vATIS's local websocket API. Like the original script, each request uses its own short-lived
/// connection (some calls are fire-and-forget, and connectAtis may not reply at all).
/// </summary>
/// <param name="trace">Told every message sent to and received from vATIS, as it happens, for the activity log.</param>
public sealed class VatisClient(IVatisSocketFactory factory, Action<string>? trace = null) : IVatisClient
{
    private static readonly TimeSpan ReplyTimeout = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Waits until vATIS is up and answers getStations (it can take a while after launch).
    /// Returns false if it never does within <paramref name="limit"/>.
    /// </summary>
    public async Task<bool> WaitUntilReadyAsync(TimeSpan limit, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        string? lastFailure = null;
        while (sw.Elapsed < limit)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var reply = await RequestAsync(new { type = "getStations" }, IsStationsReply, ReplyTimeout, ct)
                    .ConfigureAwait(false);
                if (reply is not null) return true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // vATIS not listening yet. Said once per kind of failure, not every half second.
                var failure = $"{ex.GetType().Name}: {ex.Message}";
                if (failure != lastFailure) trace?.Invoke($"vATIS not ready yet ({failure}); retrying every 500 ms");
                lastFailure = failure;
            }
            await Task.Delay(TimeSpan.FromMilliseconds(500), ct).ConfigureAwait(false);
        }
        return false;
    }

    public async Task<IReadOnlyList<VatisStation>> GetStationsAsync(CancellationToken ct = default)
    {
        // vATIS can answer with another message type while it is still loading, so ask a few times.
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var reply = await RequestOrThrowAsync(new { type = "getStations" }, IsStationsReply, ct)
                .ConfigureAwait(false);
            if (reply is not null) return ParseStations(reply);
            await Task.Delay(TimeSpan.FromMilliseconds(200), ct).ConfigureAwait(false);
        }
        throw new VatisUnavailableException("vATIS did not return its station list.");
    }

    public async Task<VatisAtisState> GetAtisAsync(string stationId, CancellationToken ct = default)
    {
        var reply = await RequestOrThrowAsync(
            new { type = "getAtis", value = new { id = stationId } },
            n => n["value"] is JsonObject, ct).ConfigureAwait(false)
            ?? throw new VatisUnavailableException($"vATIS did not answer getAtis for {stationId}.");

        var v = reply["value"];
        return new VatisAtisState(
            Str(v, "networkConnectionStatus") ?? "",
            Str(v, "airportConditions") ?? "",
            Str(v, "notams") ?? "");
    }

    /// <summary>
    /// Contractions as (variable name, text), e.g. ("RWY", "RUNWAY"). Empty if vATIS has none or returns an error
    /// (for example when it has duplicate variable names).
    /// </summary>
    public async Task<IReadOnlyList<KeyValuePair<string, string>>> GetContractionsAsync(
        string stationKey, CancellationToken ct = default)
    {
        if (stationKey.Length < 4) return [];
        object payload = stationKey.EndsWith("_D") ? new { station = stationKey[..4], atisType = "Departure" }
            : stationKey.EndsWith("_A") ? new { station = stationKey[..4], atisType = "Arrival" }
            : new { station = stationKey[..4] };

        var reply = await RequestOrThrowAsync(new { type = "getContractions", value = payload }, _ => true, ct)
            .ConfigureAwait(false);

        // Read defensively: whatever answers on the vATIS port, a reply of the wrong shape means "no contractions".
        var first = (reply?["stations"] as JsonArray)?.FirstOrDefault() as JsonObject;
        if (first?["contractions"] is not JsonObject contractions) return [];

        var result = new List<KeyValuePair<string, string>>();
        foreach (var (variable, node) in contractions)
            if (Str(node, "text") is { } text)
                result.Add(new(variable, text));
        return result;
    }

    public Task ConfigureAtisAsync(string stationId, string airportConditions, string notams,
        bool syncAtisLetter, CancellationToken ct = default) =>
        SendOnlyAsync(new
        {
            type = "configureAtis",
            value = new
            {
                id = stationId,
                preset = "D-ATIS",
                syncAtisLetter,
                airportConditionsFreeText = airportConditions,
                notamsFreeText = notams,
            },
        }, ct);

    /// <summary>Returns true if vATIS acknowledged the request.</summary>
    public async Task<bool> ConnectAtisAsync(string stationId, CancellationToken ct = default) =>
        await RequestOrThrowAsync(new { type = "connectAtis", value = new { id = stationId } }, _ => true, ct)
            .ConfigureAwait(false) is not null;

    public Task DisconnectAtisAsync(string stationId, CancellationToken ct = default) =>
        SendOnlyAsync(new { type = "disconnectAtis", value = new { id = stationId } }, ct);

    // ---- plumbing ----

    private static bool IsStationsReply(JsonNode n) => Str(n, "type") == "stations" && n["stations"] is JsonArray;

    /// <summary>The string under <paramref name="name"/>, or null if the node is not an object or the value is not a string.</summary>
    private static string? Str(JsonNode? node, string name) =>
        node is JsonObject o && o.TryGetPropertyValue(name, out var v) ? AsString(v) : null;

    private static string? AsString(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var s) ? s : null;

    private static IReadOnlyList<VatisStation> ParseStations(JsonNode reply)
    {
        var list = new List<VatisStation>();
        foreach (var s in reply["stations"]!.AsArray())
        {
            var id = Str(s, "id");
            var name = Str(s, "name");
            if (id is null || name is null) continue;
            var presets = (s!["presets"] as JsonArray)?.Select(AsString).OfType<string>().ToList() ?? [];
            list.Add(new VatisStation(id, name, Str(s, "atisType") ?? "", presets));
        }
        return list;
    }

    private async Task SendOnlyAsync(object request, CancellationToken ct)
    {
        try
        {
            await using var socket = await factory.ConnectAsync(ct).ConfigureAwait(false);
            var json = JsonSerializer.Serialize(request);
            trace?.Invoke($"-> vATIS (no reply expected) {json}");
            await socket.SendAsync(json, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            trace?.Invoke($"vATIS send failed: {ex.GetType().Name}: {ex.Message}");
            throw new VatisUnavailableException("Could not reach vATIS.", ex);
        }
    }

    private async Task<JsonNode?> RequestOrThrowAsync(
        object request, Func<JsonNode, bool> accept, CancellationToken ct)
    {
        try
        {
            return await RequestAsync(request, accept, ReplyTimeout, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not VatisUnavailableException)
        {
            trace?.Invoke($"vATIS request failed: {ex.GetType().Name}: {ex.Message}");
            throw new VatisUnavailableException("Could not reach vATIS.", ex);
        }
    }

    /// <summary>Sends a request and returns the first reply accepted by <paramref name="accept"/>, or null on timeout.</summary>
    private async Task<JsonNode?> RequestAsync(
        object request, Func<JsonNode, bool> accept, TimeSpan timeout, CancellationToken ct)
    {
        await using var socket = await factory.ConnectAsync(ct).ConfigureAwait(false);
        var json = JsonSerializer.Serialize(request);
        trace?.Invoke($"-> vATIS {json}");
        await socket.SendAsync(json, ct).ConfigureAwait(false);

        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            var text = await socket.ReceiveAsync(timeout - sw.Elapsed, ct).ConfigureAwait(false);
            if (text is null)
            {
                trace?.Invoke($"<- vATIS (nothing more within {timeout.TotalMilliseconds:0} ms)");
                return null;
            }
            JsonNode? node;
            try { node = JsonNode.Parse(text); }
            catch (JsonException)
            {
                trace?.Invoke($"<- vATIS (not JSON, ignored) {text}");
                continue;
            }
            var accepted = node is JsonObject && accept(node);
            trace?.Invoke($"<- vATIS{(accepted ? "" : " (not the reply asked for, skipped)")} {text}");
            if (accepted) return node;
        }
        trace?.Invoke($"<- vATIS (no matching reply within {timeout.TotalMilliseconds:0} ms)");
        return null;
    }
}
