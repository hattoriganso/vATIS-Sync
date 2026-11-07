using System.Reflection;
using System.Text.Json;

namespace VatisSync.Core;

/// <summary>
/// Per-airport text cleanup rules, read from local files only (nothing is downloaded):
/// <list type="number">
/// <item>the rules built into the app (<c>Rules/replacements.json</c>, embedded), then</item>
/// <item>the user's own file, by default <c>%APPDATA%\VatisSync\replacements.json</c>.</item>
/// </list>
/// For an airport, built-in rules apply first and the user's after them. The user's file is re-read
/// whenever it changes, so edits take effect on the next refresh without restarting.
/// <para>
/// File format: <c>{"replacements": {"KSFO": {"regex": "replacement"}}}</c>. Keys are regular expressions
/// (.NET syntax); an empty replacement removes the match, along with any trailing <c>, . ;</c>.
/// </para>
/// </summary>
public sealed class LocalReplacementStore : IReplacementSource
{
    private readonly string? _userFilePath;
    private readonly Dictionary<string, ReplacementRules> _builtIn;

    private (DateTime WriteTimeUtc, long Length)? _userFileStamp;
    private Dictionary<string, ReplacementRules> _user = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, ReplacementRules> _merged;

    /// <summary>Raised (not thrown) when the user's file can't be read or parsed. The last good copy stays in use.</summary>
    public event Action<string>? Warning;

    /// <param name="userFilePath">Null disables the user file.</param>
    /// <param name="builtInJson">Rules to use instead of the embedded ones (for tests).</param>
    public LocalReplacementStore(string? userFilePath = null, string? builtInJson = null)
    {
        _userFilePath = userFilePath;
        _builtIn = Parse(builtInJson ?? ReadEmbedded()) ?? new(StringComparer.OrdinalIgnoreCase);
        _merged = Merge(_builtIn, _user);
    }

    public static string DefaultUserFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VatisSync", "replacements.json");

    public Task<IReadOnlyDictionary<string, ReplacementRules>> GetAsync(
        IEnumerable<string> airports, CancellationToken ct = default)
    {
        ReloadUserFileIfChanged();

        var result = new Dictionary<string, ReplacementRules>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in airports.Distinct(StringComparer.OrdinalIgnoreCase))
            if (_merged.TryGetValue(a, out var rules)) result[a] = rules;
        return Task.FromResult<IReadOnlyDictionary<string, ReplacementRules>>(result);
    }

    private void ReloadUserFileIfChanged()
    {
        if (_userFilePath is null) return;

        (DateTime, long)? stamp = null;
        try
        {
            var info = new FileInfo(_userFilePath);
            if (info.Exists) stamp = (info.LastWriteTimeUtc, info.Length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }

        if (stamp == _userFileStamp) return;
        _userFileStamp = stamp;

        if (stamp is null)
        {
            // File removed (or never existed): no user rules.
            _user = new(StringComparer.OrdinalIgnoreCase);
        }
        else
        {
            try
            {
                var parsed = Parse(File.ReadAllText(_userFilePath));
                if (parsed is null)
                {
                    Warning?.Invoke($"Ignoring {_userFilePath}: not a valid rules file " +
                                    "(expected {\"replacements\": {\"KSFO\": {\"regex\": \"replacement\"}}}).");
                    return; // keep the last good user rules
                }
                _user = parsed;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Warning?.Invoke($"Could not read {_userFilePath}: {ex.Message}");
                return;
            }
        }
        _merged = Merge(_builtIn, _user);
    }

    private static Dictionary<string, ReplacementRules> Merge(
        Dictionary<string, ReplacementRules> builtIn, Dictionary<string, ReplacementRules> user)
    {
        var merged = new Dictionary<string, ReplacementRules>(builtIn, StringComparer.OrdinalIgnoreCase);
        foreach (var (airport, rules) in user)
            merged[airport] = merged.TryGetValue(airport, out var existing) ? [.. existing, .. rules] : rules;
        return merged;
    }

    /// <summary>Returns null if the JSON isn't a valid rules file.</summary>
    public static Dictionary<string, ReplacementRules>? Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                !doc.RootElement.TryGetProperty("replacements", out var reps) ||
                reps.ValueKind != JsonValueKind.Object)
                return null;

            var all = new Dictionary<string, ReplacementRules>(StringComparer.OrdinalIgnoreCase);
            foreach (var airport in reps.EnumerateObject())
            {
                if (airport.Value.ValueKind != JsonValueKind.Object) continue;
                var rules = new List<KeyValuePair<string, string>>();
                foreach (var rule in airport.Value.EnumerateObject())
                    if (rule.Value.ValueKind == JsonValueKind.String)
                        rules.Add(new(rule.Name, rule.Value.GetString()!));
                all[airport.Name] = rules;
            }
            return all;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string ReadEmbedded()
    {
        var assembly = typeof(LocalReplacementStore).Assembly;
        var name = assembly.GetManifestResourceNames().Single(n => n.EndsWith("replacements.json", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}

/// <summary>Use when cleanup rules are turned off.</summary>
public sealed class NoReplacements : IReplacementSource
{
    public Task<IReadOnlyDictionary<string, ReplacementRules>> GetAsync(
        IEnumerable<string> airports, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyDictionary<string, ReplacementRules>>(
            new Dictionary<string, ReplacementRules>());
}
