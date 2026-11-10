using VatisSync.Core;

namespace VatisSync.Tests;

public class MessagesTests
{
    [Fact]
    public void Every_message_the_code_asks_for_has_a_text_in_the_file()
    {
        var missing = Msg.All.Where(k => !Messages.Raw.ContainsKey(k.Id)).Select(k => k.Id).ToList();
        Assert.True(missing.Count == 0, "No text in messages.json for: " + string.Join(", ", missing));
    }

    [Fact]
    public void Every_text_in_the_file_is_used_by_the_code()
    {
        var known = Msg.All.Select(k => k.Id).ToHashSet();
        var unused = Messages.Raw.Keys.Where(k => !known.Contains(k)).ToList();
        Assert.True(unused.Count == 0, "In messages.json but never used (typo in the key, or a leftover): " + string.Join(", ", unused));
    }

    [Fact]
    public void Message_keys_are_unique()
    {
        var ids = Msg.All.Select(k => k.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    [Fact]
    public void No_text_uses_a_placeholder_the_code_does_not_provide()
    {
        // A misspelled placeholder such as {secnds} would otherwise appear on screen as-is.
        var problems = new List<string>();
        foreach (var key in Msg.All)
        {
            var text = Messages.Raw[key.Id];
            var unknown = Messages.PlaceholdersIn(text).Where(p => !key.Placeholders.Contains(p)).ToList();
            if (unknown.Count > 0)
                problems.Add($"{key.Id}: {{{string.Join("}, {", unknown)}}} (available: {string.Join(", ", key.Placeholders.DefaultIfEmpty("none"))})");
        }
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    [Fact]
    public void No_text_leaves_a_brace_behind_once_filled()
    {
        foreach (var key in Msg.All)
        {
            var values = key.Placeholders.ToDictionary(p => p, _ => (object?)"x");
            var filled = Messages.Fill(Messages.Raw[key.Id], values);
            Assert.DoesNotContain("{", filled);
            Assert.DoesNotContain("}", filled);
        }
    }

    [Fact]
    public void The_user_never_has_to_know_about_websockets()
    {
        foreach (var (key, text) in Messages.Raw)
            Assert.DoesNotContain("websocket", text, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(14, "Couldn't sync with vATIS, retrying in 14 seconds.")]
    [InlineData(1, "Couldn't sync with vATIS, retrying in 1 second.")]
    public void Placeholders_and_plurals_are_filled_in(int seconds, string expected) =>
        Assert.Equal(expected, Messages.Get(Msg.DiscoveryWaiting, new { seconds }));

    [Fact]
    public void A_plural_can_swap_a_whole_phrase()
    {
        Assert.Equal("1 airport is no longer in your vATIS profile.", Messages.Get(Msg.DiscoveryStale, new { count = 1 }));
        Assert.Equal("3 airports are no longer in your vATIS profile.", Messages.Get(Msg.DiscoveryStale, new { count = 3 }));
    }

    [Fact]
    public void Messages_without_placeholders_come_back_as_written()
    {
        Assert.Equal("Looking for vATIS…", Messages.Get(Msg.DiscoverySearching));
    }

    [Fact]
    public void A_placeholder_with_no_value_stays_visible_and_an_unknown_key_gives_its_name()
    {
        Assert.Equal("Connected {station}.", Messages.Get(Msg.EngineConnected, new { other = 1 }));
        Assert.Equal("no.such.key", Messages.Get(new MessageKey("no.such.key")));
    }
}
