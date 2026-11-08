using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VatisSync.Core;

/// <summary>A message the app can show: its name in messages.json and the placeholders its text may use.</summary>
public sealed record MessageKey(string Id, params string[] Placeholders);

/// <summary>
/// The messages the app shows to the user, by name. The texts themselves are in <c>Messages/messages.json</c>, not in code,
/// so they can be read and reworded in one place. A test checks that every key here has a text and that no text uses a
/// placeholder the code does not provide.
/// </summary>
public static class Msg
{
    // Reading the airports from the vATIS profile
    public static readonly MessageKey DiscoverySearching = new("discovery.searching");
    public static readonly MessageKey DiscoverySearchingAgain = new("discovery.searching-again");
    public static readonly MessageKey DiscoveryWaiting = new("discovery.waiting", "seconds");
    public static readonly MessageKey DiscoveryWaitingBar = new("discovery.waiting-bar", "seconds");
    public static readonly MessageKey DiscoveryTryingBar = new("discovery.trying-bar");
    public static readonly MessageKey DiscoveryFound = new("discovery.found");
    public static readonly MessageKey DiscoveryAttemptFailed = new("discovery.attempt-failed", "problem", "attempt", "seconds");
    public static readonly MessageKey DiscoveryProblem = new("discovery.problem");
    public static readonly MessageKey DiscoveryRead = new("discovery.read", "count", "new", "failed");
    public static readonly MessageKey DiscoveryEmpty = new("discovery.empty");
    public static readonly MessageKey DiscoveryAdded = new("discovery.added", "count");
    public static readonly MessageKey DiscoveryStale = new("discovery.stale", "count");
    public static readonly MessageKey DiscoveryMatches = new("discovery.matches");

    // Status line
    public static readonly MessageKey StatusStoppedHeadline = new("status.stopped.headline");
    public static readonly MessageKey StatusStoppedDetail = new("status.stopped.detail");
    public static readonly MessageKey StatusStartingHeadline = new("status.starting.headline");
    public static readonly MessageKey StatusStartingDetail = new("status.starting.detail");
    public static readonly MessageKey StatusWaitingHeadline = new("status.waiting.headline");
    public static readonly MessageKey StatusWaitingDetail = new("status.waiting.detail");
    public static readonly MessageKey StatusSyncingHeadline = new("status.syncing.headline");
    public static readonly MessageKey StatusSyncingDetail = new("status.syncing.detail");
    public static readonly MessageKey StatusIdleHeadline = new("status.idle.headline");
    public static readonly MessageKey StatusIdleDetail = new("status.idle.detail");

    // The sync engine
    public static readonly MessageKey EngineStarting = new("engine.starting");
    public static readonly MessageKey EngineWaiting = new("engine.waiting");
    public static readonly MessageKey EngineLoading = new("engine.loading");
    public static readonly MessageKey EngineRefreshing = new("engine.refreshing");
    public static readonly MessageKey EngineUnreachable = new("engine.unreachable");
    public static readonly MessageKey EngineUnreachableLog = new("engine.unreachable-log", "reason");
    public static readonly MessageKey EngineUnexpected = new("engine.unexpected");
    public static readonly MessageKey EngineUnexpectedLog = new("engine.unexpected-log", "error");
    public static readonly MessageKey EngineVatisNotFound = new("engine.vatis-not-found");
    public static readonly MessageKey EngineDatisFailedCached = new("engine.datis-failed-cached", "reason");
    public static readonly MessageKey EngineDatisFailedNone = new("engine.datis-failed-none", "reason");
    public static readonly MessageKey EngineWaitingApproval = new("engine.waiting-approval", "station");
    public static readonly MessageKey EngineApprovalKept = new("engine.approval-kept", "station", "summary");
    public static readonly MessageKey EngineApprovalApplied = new("engine.approval-applied", "station", "summary");
    public static readonly MessageKey EngineConnected = new("engine.connected", "station");
    public static readonly MessageKey EngineDisconnectedLimit = new("engine.disconnected-limit", "station", "limit");
    public static readonly MessageKey EngineKeepingDeletions = new("engine.keeping-deletions", "count", "station");

    // Settings, rules and the app itself
    public static readonly MessageKey SettingsReadFailed = new("settings.read-failed", "path", "reason");
    public static readonly MessageKey SettingsSaveFailed = new("settings.save-failed", "path", "reason");
    public static readonly MessageKey RulesInvalidFile = new("rules.invalid-file", "path");
    public static readonly MessageKey RulesReadFailed = new("rules.read-failed", "path", "reason");
    public static readonly MessageKey RulesOpenFailed = new("rules.open-failed", "path", "reason");
    public static readonly MessageKey StartupFailed = new("startup.failed");
    public static readonly MessageKey ConnectInvalidAirport = new("connect.invalid-airport", "text");
    public static readonly MessageKey AppUnexpected = new("app.unexpected", "path");
    public static readonly MessageKey AppUnexpectedNoFile = new("app.unexpected-no-file");
    public static readonly MessageKey AppUnexpectedLog = new("app.unexpected-log", "type", "message");

    /// <summary>Every key above (used by the test that checks them against the file).</summary>
    public static IReadOnlyList<MessageKey> All { get; } = typeof(Msg)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(f => f.FieldType == typeof(MessageKey))
        .Select(f => (MessageKey)f.GetValue(null)!)
        .ToList();
}

/// <summary>Looks up and fills in the texts in <c>Messages/messages.json</c>.</summary>
public static class Messages
{
    private static readonly Lazy<IReadOnlyDictionary<string, string>> Texts = new(Load);

    // {name} or {name?one|many}
    private static readonly Regex Placeholder = new(@"\{(?<name>[A-Za-z][A-Za-z0-9]*)(?:\?(?<one>[^|}]*)\|(?<many>[^}]*))?\}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// The message with its placeholders filled in from the properties of <paramref name="values"/>, for example
    /// <c>Messages.Get(Msg.DiscoveryWaiting, new { seconds = 14 })</c>. A key with no text gives the key itself, so a gap
    /// is visible rather than blank.
    /// </summary>
    public static string Get(MessageKey key, object? values = null) =>
        Fill(Texts.Value.TryGetValue(key.Id, out var text) ? text : key.Id, values);

    /// <summary>The raw texts, by key (for tests and for tooling).</summary>
    public static IReadOnlyDictionary<string, string> Raw => Texts.Value;

    /// <summary>The placeholder names a text uses.</summary>
    public static IEnumerable<string> PlaceholdersIn(string text) =>
        Placeholder.Matches(text).Select(m => m.Groups["name"].Value).Distinct();

    public static string Fill(string text, object? values)
    {
        if (values is null) return text;
        var props = values as IReadOnlyDictionary<string, object?>
            ?? values.GetType().GetProperties().ToDictionary(p => p.Name, p => p.GetValue(values));
        return Placeholder.Replace(text, m =>
        {
            if (!props.TryGetValue(m.Groups["name"].Value, out var value)) return m.Value; // leave it visible
            if (m.Groups["one"].Success)
                return IsOne(value) ? m.Groups["one"].Value : m.Groups["many"].Value;
            return Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "";
        });
    }

    private static bool IsOne(object? value) => value switch
    {
        int i => i == 1,
        long l => l == 1,
        double d => d == 1,
        decimal m => m == 1,
        string s => s == "1",
        _ => false,
    };

    private static IReadOnlyDictionary<string, string> Load()
    {
        var asm = Assembly.GetExecutingAssembly();
        var name = asm.GetManifestResourceNames().Single(n => n.EndsWith("messages.json", StringComparison.Ordinal));
        using var reader = new StreamReader(asm.GetManifestResourceStream(name)!);
        using var doc = JsonDocument.Parse(reader.ReadToEnd(), new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        });
        return doc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString() ?? "");
    }
}
