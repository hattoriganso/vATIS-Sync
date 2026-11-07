using System.Net;
using System.Text.Json.Nodes;
using VatisSync.Core;

namespace VatisSync.Tests;

/// <summary>A scratch directory removed when the test finishes.</summary>
public abstract class TempDirTests : IDisposable
{
    protected string Dir { get; } = Path.Combine(Path.GetTempPath(), "vatissync-tests-" + Guid.NewGuid().ToString("N"));

    protected TempDirTests() => Directory.CreateDirectory(Dir);

    public void Dispose()
    {
        try { Directory.Delete(Dir, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    protected string Write(string relativePath, string content)
    {
        var path = Path.Combine(Dir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }
}

public class CrcPositionResolverTests : TempDirTests
{
    private const string Artcc = """
        {"facility":{
          "positions":[{"id":"p-center","callsign":"OAK_CTR"}],
          "childFacilities":[
            {"positions":[{"id":"p-app","callsign":"NCT_APP"}],
             "childFacilities":[
               {"positions":[{"id":"p-twr","callsign":"SFO_TWR"},{"id":"p-multi","callsign":"SFO_1_GND"},{"id":"p-bad","callsign":"NOUNDERSCORE"}],
                "childFacilities":[]}]}]}}
        """;

    private void Profile(string file, string? lastUsed, string positionId, string artcc = "ZOA") =>
        Write($"Profiles/{file}.json",
            new JsonObject
            {
                ["Name"] = file,
                ["ArtccId"] = artcc,
                ["LastUsedPositionId"] = positionId,
                ["LastUsedAt"] = lastUsed,
            }.ToJsonString());

    private CrcPosition? Resolve() => new CrcPositionResolver(Dir).GetActivePosition();

    [Theory]
    [InlineData("SFO_TWR", "SFO", "TWR")]
    [InlineData("SFO_1_GND", "SFO", "GND")]
    [InlineData("A_B_C_D", "A", "D")]
    public void SplitCallsign_uses_first_and_last_underscore(string callsign, string prefix, string suffix) =>
        Assert.Equal(new CrcPosition(prefix, suffix), CrcPositionResolver.SplitCallsign(callsign));

    [Fact]
    public void SplitCallsign_without_underscore_is_null() =>
        Assert.Null(CrcPositionResolver.SplitCallsign("NOUNDERSCORE"));

    [Fact]
    public void Finds_a_position_in_a_nested_child_facility()
    {
        Write("ARTCCs/ZOA.json", Artcc);
        Profile("a", "2026-11-01T10:00:00.123Z", "p-twr");
        Assert.Equal(new CrcPosition("SFO", "TWR"), Resolve());
    }

    [Fact]
    public void Finds_a_position_at_the_top_level()
    {
        Write("ARTCCs/ZOA.json", Artcc);
        Profile("a", "2026-11-01T10:00:00Z", "p-center");
        Assert.Equal(new CrcPosition("OAK", "CTR"), Resolve());
    }

    [Fact]
    public void Newest_profile_wins()
    {
        Write("ARTCCs/ZOA.json", Artcc);
        Profile("old", "2026-10-01T10:00:00Z", "p-center");
        Profile("new", "2026-11-05T10:00:00Z", "p-app");
        Profile("older", "2025-01-01T10:00:00Z", "p-twr");
        Assert.Equal(new CrcPosition("NCT", "APP"), Resolve());
    }

    [Fact]
    public void Profiles_never_used_are_ignored()
    {
        Write("ARTCCs/ZOA.json", Artcc);
        Profile("never", null, "p-twr");
        Assert.Null(Resolve());
    }

    [Fact]
    public void Malformed_profile_is_skipped_not_fatal()
    {
        Write("ARTCCs/ZOA.json", Artcc);
        Write("Profiles/broken.json", "{ not json");
        Profile("good", "2026-11-01T10:00:00Z", "p-twr");
        Assert.Equal(new CrcPosition("SFO", "TWR"), Resolve());
    }

    [Fact]
    public void Unknown_position_or_callsign_without_underscore_is_null()
    {
        Write("ARTCCs/ZOA.json", Artcc);
        Profile("a", "2026-11-01T10:00:00Z", "p-missing");
        Assert.Null(Resolve());
        Profile("a", "2026-11-01T10:00:00Z", "p-bad");
        Assert.Null(Resolve());
    }

    [Fact]
    public void Missing_directories_or_artcc_file_give_null()
    {
        Assert.Null(Resolve()); // no Profiles folder at all
        Profile("a", "2026-11-01T10:00:00Z", "p-twr"); // profile but no ARTCCs\ZOA.json
        Assert.Null(Resolve());
    }

    [Fact]
    public void Artcc_file_with_a_utf8_bom_is_read()
    {
        File.WriteAllText(Path.Combine(Dir, "ARTCCs_tmp.json"), "");
        Directory.CreateDirectory(Path.Combine(Dir, "ARTCCs"));
        File.WriteAllText(Path.Combine(Dir, "ARTCCs", "ZOA.json"), Artcc, new System.Text.UTF8Encoding(true));
        Profile("a", "2026-11-01T10:00:00Z", "p-twr");
        Assert.Equal(new CrcPosition("SFO", "TWR"), Resolve());
    }
}

public class VatisLauncherTests : TempDirTests
{
    [Fact]
    public void Turns_autoFetchAtisLetter_on_and_keeps_other_settings()
    {
        var path = Write("AppConfig.json", """{"autoFetchAtisLetter":false,"theme":"dark","n":3}""");
        Assert.True(VatisLauncher.EnableAutoFetchAtisLetter(path));

        var json = JsonNode.Parse(File.ReadAllText(path))!;
        Assert.True((bool)json["autoFetchAtisLetter"]!);
        Assert.Equal("dark", (string?)json["theme"]);
        Assert.Equal(3, (int)json["n"]!);
    }

    [Fact]
    public void Leaves_the_file_alone_when_already_on()
    {
        var content = """{"autoFetchAtisLetter":true}""";
        var path = Write("AppConfig.json", content);
        Assert.False(VatisLauncher.EnableAutoFetchAtisLetter(path));
        Assert.Equal(content, File.ReadAllText(path));
    }

    [Fact]
    public void Does_not_add_the_key_when_vatis_does_not_have_it()
    {
        var content = """{"theme":"dark"}""";
        var path = Write("AppConfig.json", content);
        Assert.False(VatisLauncher.EnableAutoFetchAtisLetter(path));
        Assert.Equal(content, File.ReadAllText(path));
    }

    [Fact]
    public void Missing_or_corrupt_config_never_throws()
    {
        Assert.False(VatisLauncher.EnableAutoFetchAtisLetter(Path.Combine(Dir, "nope.json")));
        var bad = Write("bad.json", "{ nope");
        Assert.False(VatisLauncher.EnableAutoFetchAtisLetter(bad));
        Assert.Equal("{ nope", File.ReadAllText(bad));
    }

    [Fact]
    public void Paths_follow_the_install_directory()
    {
        var l = new VatisLauncher(Dir);
        Assert.Equal(Path.Combine(Dir, "AppConfig.json"), l.ConfigPath);
        Assert.Equal(Path.Combine(Dir, "current", "vATIS.exe"), l.ExePath);
    }

    [Fact]
    public void Reports_NotFound_when_vatis_is_not_installed_there()
    {
        // Only meaningful when no real vATIS process is running; otherwise AlreadyRunning is correct.
        var result = new VatisLauncher(Dir).EnsureRunning();
        Assert.True(result is LaunchResult.NotFound or LaunchResult.AlreadyRunning);
    }
}
