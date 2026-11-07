using System.Globalization;
using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Win32;

namespace VatisSync.Core;

/// <summary>
/// Works out which position the user last used in CRC, by reading CRC's own files:
/// the newest <c>Profiles\*.json</c> gives the ARTCC and last position id, and
/// <c>ARTCCs\&lt;ARTCC&gt;.json</c> maps that id to a callsign. Read-only.
/// </summary>
public sealed class CrcPositionResolver : IActivePositionProvider
{
    private static readonly DateTimeOffset Epoch = new(2020, 1, 1, 8, 0, 0, TimeSpan.Zero);

    private readonly string? _root;

    /// <param name="crcRoot">CRC install directory. Null means: registry value, else %LOCALAPPDATA%\CRC.</param>
    public CrcPositionResolver(string? crcRoot = null) => _root = crcRoot;

    public CrcPosition? GetActivePosition()
    {
        var root = _root ?? DefaultRoot();
        if (root is null) return null;

        var profile = NewestProfile(Path.Combine(root, "Profiles"));
        if (profile is null) return null;

        try
        {
            var p = profile.Value;
            if (p.TryGetProperty("LastUsedPositionId", out var posEl) && posEl.GetString() is { } positionId &&
                p.TryGetProperty("ArtccId", out var artccEl) && artccEl.GetString() is { } artcc)
            {
                var file = Path.Combine(root, "ARTCCs", artcc + ".json");
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                if (doc.RootElement.TryGetProperty("facility", out var facility))
                    return FindPosition(facility, positionId);
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException
                                       or UnauthorizedAccessException)
        {
            // CRC files missing or in an unexpected shape: no active position.
        }
        return null;
    }

    /// <summary>"SFO_TWR" -> ("SFO","TWR"); "SFO_1_TWR" -> ("SFO","TWR"); no underscore -> null.</summary>
    public static CrcPosition? SplitCallsign(string callsign)
    {
        var first = callsign.IndexOf('_');
        var last = callsign.LastIndexOf('_');
        return first < 0 ? null : new CrcPosition(callsign[..first], callsign[(last + 1)..]);
    }

    private static CrcPosition? FindPosition(JsonElement facility, string positionId)
    {
        if (facility.TryGetProperty("positions", out var positions) && positions.ValueKind == JsonValueKind.Array)
        {
            foreach (var pos in positions.EnumerateArray())
            {
                if (pos.TryGetProperty("id", out var id) && id.GetString() == positionId)
                    return pos.TryGetProperty("callsign", out var cs) && cs.GetString() is { } callsign
                        ? SplitCallsign(callsign)
                        : null;
            }
        }

        if (facility.TryGetProperty("childFacilities", out var children) && children.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in children.EnumerateArray())
                if (FindPosition(child, positionId) is { } found) return found;
        }
        return null;
    }

    /// <summary>The profile with the newest LastUsedAt (after 2020-01-01). Unreadable files are skipped.</summary>
    private static JsonElement? NewestProfile(string profilesDir)
    {
        if (!Directory.Exists(profilesDir)) return null;

        JsonElement? best = null;
        var bestTime = Epoch;
        foreach (var file in Directory.EnumerateFiles(profilesDir, "*.json"))
        {
            try
            {
                var doc = JsonDocument.Parse(File.ReadAllText(file));
                var root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.Object &&
                    root.TryGetProperty("LastUsedAt", out var at) && at.ValueKind == JsonValueKind.String &&
                    DateTimeOffset.TryParse(at.GetString(), CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var t) &&
                    t > bestTime)
                {
                    bestTime = t;
                    best = root.Clone();
                }
                doc.Dispose();
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
        }
        return best;
    }

    private static string? DefaultRoot()
    {
        if (OperatingSystem.IsWindows() && RegistryInstallDir() is { Length: > 0 } fromRegistry)
            return fromRegistry;
        var local = Environment.GetEnvironmentVariable("LOCALAPPDATA");
        return local is null ? null : Path.Combine(local, "CRC");
    }

    [SupportedOSPlatform("windows")]
    private static string? RegistryInstallDir()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\CRC");
            return key?.GetValue("Install_Dir") as string;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
