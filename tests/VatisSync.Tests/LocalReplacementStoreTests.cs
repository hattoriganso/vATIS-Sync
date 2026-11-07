using VatisSync.Core;

namespace VatisSync.Tests;

public class LocalReplacementStoreTests : TempDirTests
{
    private const string BuiltIn = """
        {"replacements":{
          "KSFO":{"CLSD":"CLOSED","TFR":"TFR%r"},
          "KOAK":{"OTS":"OUT OF SERVICE"}
        }}
        """;

    private string UserFile => Path.Combine(Dir, "user", "replacements.json");

    private LocalReplacementStore Store(string? userPath = null, string builtIn = BuiltIn) =>
        new(userPath ?? UserFile, builtIn);

    private static async Task<ReplacementRules> Rules(LocalReplacementStore s, string airport) =>
        (await s.GetAsync([airport])).TryGetValue(airport, out var r) ? r : [];

    // ---- parsing ----

    [Fact]
    public void Parse_keeps_rule_order()
    {
        var all = LocalReplacementStore.Parse(BuiltIn)!;
        Assert.Equal(["CLSD", "TFR"], all["KSFO"].Select(r => r.Key));
        Assert.Equal("OUT OF SERVICE", all["KOAK"].Single().Value);
    }

    [Fact]
    public void Parse_skips_malformed_airports_and_non_string_rules()
    {
        var all = LocalReplacementStore.Parse("""{"replacements":{"KSFO":{"A":"B","C":5}, "KOAK":[1]}}""")!;
        Assert.Equal([new("A", "B")], all["KSFO"]);
        Assert.False(all.ContainsKey("KOAK"));
    }

    [Fact]
    public void Parse_allows_comments_and_trailing_commas_since_users_edit_this_by_hand()
    {
        var all = LocalReplacementStore.Parse("""
            { // my rules
              "replacements": {
                "KSFO": { "CLSD": "CLOSED", },
              },
            }
            """)!;
        Assert.Equal([new("CLSD", "CLOSED")], all["KSFO"]);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("""{"other":1}""")]
    [InlineData("""{"replacements":[]}""")]
    public void Parse_rejects_wrong_shapes(string json) => Assert.Null(LocalReplacementStore.Parse(json));

    // ---- built-in rules ----

    [Fact]
    public async Task Embedded_built_in_file_is_present_and_valid()
    {
        // Proves the resource is compiled in and parses; it ships empty until we author rules.
        var store = new LocalReplacementStore(userFilePath: null);
        Assert.Empty(await store.GetAsync(["KSFO", "KMIA"]));
    }

    [Fact]
    public async Task Returns_only_requested_airports_case_insensitively()
    {
        var result = await Store().GetAsync(["ksfo", "KJFK", "KSFO"]);
        Assert.Equal(["ksfo"], result.Keys);
        Assert.Equal(2, result["ksfo"].Count);
    }

    [Fact]
    public async Task Works_with_no_user_file_present()
    {
        Assert.Equal(2, (await Rules(Store(), "KSFO")).Count);
    }

    [Fact]
    public async Task Null_user_path_means_built_in_only()
    {
        var store = new LocalReplacementStore(userFilePath: null, BuiltIn);
        Assert.Equal(2, (await Rules(store, "KSFO")).Count);
    }

    // ---- user rules ----

    [Fact]
    public async Task User_rules_apply_after_built_in_rules_for_the_same_airport()
    {
        Write("user/replacements.json", """{"replacements":{"KSFO":{"CRANE":""}}}""");
        var rules = await Rules(Store(), "KSFO");
        Assert.Equal(["CLSD", "TFR", "CRANE"], rules.Select(r => r.Key));
    }

    [Fact]
    public async Task User_can_add_an_airport_the_built_in_file_lacks()
    {
        Write("user/replacements.json", """{"replacements":{"KJFK":{"BIRD ACTIVITY.*":""}}}""");
        var store = Store();
        Assert.Single(await Rules(store, "KJFK"));
        Assert.Equal(2, (await Rules(store, "KSFO")).Count); // others unaffected
    }

    [Fact]
    public async Task Editing_the_user_file_takes_effect_without_restarting()
    {
        var store = Store();
        Assert.Equal(2, (await Rules(store, "KSFO")).Count);

        Write("user/replacements.json", """{"replacements":{"KSFO":{"A":""}}}""");
        Assert.Equal(3, (await Rules(store, "KSFO")).Count);

        Write("user/replacements.json", """{"replacements":{"KSFO":{"A":"","BB":"","CCC":""}}}""");
        Assert.Equal(5, (await Rules(store, "KSFO")).Count);
    }

    [Fact]
    public async Task Deleting_the_user_file_removes_its_rules()
    {
        var path = Write("user/replacements.json", """{"replacements":{"KSFO":{"A":""}}}""");
        var store = Store();
        Assert.Equal(3, (await Rules(store, "KSFO")).Count);

        File.Delete(path);
        Assert.Equal(2, (await Rules(store, "KSFO")).Count);
    }

    // ---- bad user files ----

    [Fact]
    public async Task Invalid_user_file_warns_once_and_keeps_the_last_good_rules()
    {
        Write("user/replacements.json", """{"replacements":{"KSFO":{"A":""}}}""");
        var store = Store();
        var warnings = new List<string>();
        store.Warning += warnings.Add;
        Assert.Equal(3, (await Rules(store, "KSFO")).Count);

        Write("user/replacements.json", "{ this is not valid json");
        Assert.Equal(3, (await Rules(store, "KSFO")).Count); // still the last good
        await Rules(store, "KSFO");
        await Rules(store, "KSFO");

        Assert.Single(warnings); // one warning per bad edit, not one per refresh
        Assert.Contains(UserFile, warnings[0]); // names the file so the user knows what to fix
    }

    [Fact]
    public async Task Fixing_a_broken_user_file_recovers()
    {
        Write("user/replacements.json", "{ broken");
        var store = Store();
        var warnings = new List<string>();
        store.Warning += warnings.Add;
        Assert.Equal(2, (await Rules(store, "KSFO")).Count);
        Assert.Single(warnings);

        Write("user/replacements.json", """{"replacements":{"KSFO":{"A":"","B":""}}}""");
        Assert.Equal(4, (await Rules(store, "KSFO")).Count);
    }

    [Fact]
    public async Task A_regex_that_does_not_compile_is_skipped_by_the_formatter_not_fatal()
    {
        Write("user/replacements.json", """{"replacements":{"KSFO":{"(":"", "KEEP ME":"KEPT"}}}""");
        var rules = await Rules(Store(), "KSFO");

        var entry = new DatisEntry("KSFO", "combined", "H1. H2. KEEP ME. ...ADVS YOU HAVE INFO A.",
            DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.ffffff"));
        var result = DatisFormatter.Format("KSFO", [entry], DateTimeOffset.UtcNow, rules);

        Assert.Equal("KEPT", result.AirportConditions.Trim());
    }

    // ---- end to end with the formatter ----

    [Fact]
    public async Task A_user_rule_changes_the_text_that_reaches_vatis()
    {
        Write("user/replacements.json", """{"replacements":{"KSFO":{"CTC GC FOR PUSH":""}}}""");
        var rules = await Rules(Store(), "KSFO");

        var entry = new DatisEntry("KSFO", "combined",
            "H1. H2. ILS RWY 28R IN USE. CTC GC FOR PUSH. TWY B CLSD. ...ADVS YOU HAVE INFO A.",
            DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.ffffff"));
        var result = DatisFormatter.Format("KSFO", [entry], DateTimeOffset.UtcNow, rules);

        Assert.DoesNotContain("CTC GC", result.AirportConditions);
        Assert.Contains("ILS RWY 28R IN USE.", result.AirportConditions);
        Assert.Contains("TWY B CLOSED", result.AirportConditions); // the built-in rule applied too
    }

    [Fact]
    public void Default_user_file_lives_under_AppData_VatisSync()
    {
        var path = LocalReplacementStore.DefaultUserFilePath;
        Assert.EndsWith(Path.Combine("VatisSync", "replacements.json"), path);
    }

    [Fact]
    public async Task Caller_cancellation_is_not_needed_for_local_reads_and_does_not_break_them()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        // Local reads are synchronous and instant, so a cancelled token is simply ignored.
        Assert.Equal(2, (await Store().GetAsync(["KSFO"], cts.Token))["KSFO"].Count);
    }
}
