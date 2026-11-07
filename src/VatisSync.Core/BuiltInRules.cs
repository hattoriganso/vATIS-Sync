using System.Reflection;
using System.Text.Json;

namespace VatisSync.Core;

/// <summary>A built-in cleanup rule that applies to every airport and can be switched off by the user.</summary>
public sealed record BuiltInRule(string Id, string Name, string Description, IReadOnlyList<string> Patterns);

/// <summary>
/// Our own cleanup rules, shipped inside the app (Rules/builtin-rules.json). The user toggles them by id;
/// <see cref="PatternsFor"/> returns the regexes of the enabled ones, as removal rules for the formatter.
/// </summary>
public static class BuiltInRules
{
    /// <summary>Placeholder for "a sentence fragment" inside rule patterns.</summary>
    public const string SentencePlaceholder = "{S}";
    private const string SentenceRegex = @"(?:[^.]|(?<=\d)\.(?=\d))*";

    public static IReadOnlyList<BuiltInRule> All { get; } = LoadEmbedded();

    /// <param name="disabledIds">Rule ids the user switched off (case-insensitive; unknown ids are ignored).</param>
    public static ReplacementRules PatternsFor(IEnumerable<string>? disabledIds = null) =>
        PatternsFor(All, disabledIds);

    public static ReplacementRules PatternsFor(IEnumerable<BuiltInRule> rules, IEnumerable<string>? disabledIds)
    {
        var disabled = new HashSet<string>(disabledIds ?? [], StringComparer.OrdinalIgnoreCase);
        return rules.Where(r => !disabled.Contains(r.Id))
            .SelectMany(r => r.Patterns)
            .Select(p => new KeyValuePair<string, string>(p.Replace(SentencePlaceholder, SentenceRegex), ""))
            .ToList();
    }

    public static IReadOnlyList<BuiltInRule> Parse(string json)
    {
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        });
        var result = new List<BuiltInRule>();
        foreach (var r in doc.RootElement.GetProperty("rules").EnumerateArray())
        {
            var patterns = r.GetProperty("patterns").EnumerateArray().Select(p => p.GetString()!).ToList();
            result.Add(new BuiltInRule(
                r.GetProperty("id").GetString()!,
                r.GetProperty("name").GetString()!,
                r.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "",
                patterns));
        }
        return result;
    }

    private static IReadOnlyList<BuiltInRule> LoadEmbedded()
    {
        var asm = Assembly.GetExecutingAssembly();
        var name = asm.GetManifestResourceNames().Single(n => n.EndsWith("builtin-rules.json", StringComparison.Ordinal));
        using var reader = new StreamReader(asm.GetManifestResourceStream(name)!);
        return Parse(reader.ReadToEnd());
    }
}
