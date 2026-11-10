using System.Text.Json.Nodes;
using VatisSync.Core;

namespace VatisSync.Tests;

public class ActivityLogTests : TempDirTests
{
    private string LogDir => Path.Combine(Dir, "logs");

    private sealed class Clock(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;
        public override DateTimeOffset GetUtcNow() => Now.ToUniversalTime();
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    private static readonly DateTimeOffset Noon = new(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Entries_carry_a_timestamp_and_a_level_and_land_in_a_file_named_for_the_day()
    {
        var log = new ActivityLog(LogDir, new Clock(Noon.AddMilliseconds(123)));
        log.Write("started");
        log.Write("update approved", LogKind.Success);
        log.Write("slow reply", LogKind.Warning);
        log.Write("it broke", LogKind.Error);
        log.Trace("raw detail");

        var file = Path.Combine(LogDir, "activity-2026-11-10.log");
        Assert.Equal(file, log.TodaysFile);
        var lines = File.ReadAllLines(file);
        Assert.Equal(5, lines.Length);
        Assert.Equal("2026-11-10 12:00:00.123  INFO   started", lines[0]);
        Assert.Contains("  OK     update approved", lines[1]);
        Assert.Contains("  WARN   slow reply", lines[2]);
        Assert.Contains("  ERROR  it broke", lines[3]);
        Assert.Contains("  TRACE  raw detail", lines[4]);
    }

    [Fact]
    public void Detail_is_only_written_while_it_is_switched_on_but_everything_else_always_is()
    {
        var log = new ActivityLog(LogDir, new Clock(Noon)) { TraceEnabled = false };
        log.Trace("hidden");
        log.Write("shown");
        log.TraceEnabled = true;
        log.Trace("now visible");

        var text = File.ReadAllText(log.TodaysFile);
        Assert.DoesNotContain("hidden", text);
        Assert.Contains("shown", text);
        Assert.Contains("now visible", text);
    }

    [Fact]
    public void An_exception_is_written_whole_with_its_stack_under_one_timestamp()
    {
        Exception error;
        try { throw new InvalidOperationException("boom"); } catch (Exception ex) { error = ex; }

        var log = new ActivityLog(LogDir, new Clock(Noon));
        log.Write(error, "background task");

        var lines = File.ReadAllLines(log.TodaysFile);
        Assert.Contains("ERROR  background task: System.InvalidOperationException: boom", lines[0]);
        Assert.True(lines.Length > 1); // the stack
        Assert.All(lines.Skip(1), l => Assert.StartsWith("    ", l)); // continuation lines are indented, not timestamped
    }

    [Fact]
    public void An_enormous_entry_is_cut()
    {
        var log = new ActivityLog(LogDir, new Clock(Noon));
        log.Write(new string('x', ActivityLog.MaxEntryChars * 3));

        var line = File.ReadAllText(log.TodaysFile);
        Assert.True(line.Length < ActivityLog.MaxEntryChars + 200);
        Assert.Contains("[cut,", line);
    }

    [Fact]
    public void A_file_past_the_size_limit_is_moved_aside_and_a_fresh_one_started()
    {
        var log = new ActivityLog(LogDir, new Clock(Noon));
        log.Write("first");
        File.AppendAllText(log.TodaysFile, new string('x', (int)ActivityLog.MaxBytes));

        log.Write("after");

        Assert.True(File.Exists(log.TodaysFile + ".old"));
        var fresh = File.ReadAllText(log.TodaysFile);
        Assert.Contains("after", fresh);
        Assert.DoesNotContain("first", fresh);
    }

    [Fact]
    public void Old_files_are_deleted_and_recent_ones_kept()
    {
        Directory.CreateDirectory(LogDir);
        var old = Path.Combine(LogDir, "activity-2026-10-01.log");
        var recent = Path.Combine(LogDir, "activity-2026-11-05.log");
        var other = Path.Combine(LogDir, "errors.log"); // not ours to prune
        foreach (var f in new[] { old, recent, other }) File.WriteAllText(f, "x");
        File.SetLastWriteTimeUtc(old, Noon.UtcDateTime.AddDays(-(ActivityLog.KeepDays + 2)));
        File.SetLastWriteTimeUtc(recent, Noon.UtcDateTime.AddDays(-3));
        File.SetLastWriteTimeUtc(other, Noon.UtcDateTime.AddDays(-100));

        new ActivityLog(LogDir, new Clock(Noon)).Write("today");

        Assert.False(File.Exists(old));
        Assert.True(File.Exists(recent));
        Assert.True(File.Exists(other));
    }

    [Fact]
    public void A_new_day_starts_a_new_file()
    {
        var clock = new Clock(Noon);
        var log = new ActivityLog(LogDir, clock);
        log.Write("day one");
        clock.Now = Noon.AddDays(1);
        log.Write("day two");

        Assert.Contains("day one", File.ReadAllText(Path.Combine(LogDir, "activity-2026-11-10.log")));
        Assert.Contains("day two", File.ReadAllText(Path.Combine(LogDir, "activity-2026-11-11.log")));
    }

    [Fact]
    public void Writing_never_throws_even_when_the_folder_cannot_be_created()
    {
        var blocker = Write("blocker", "a file where the folder should be");
        var log = new ActivityLog(Path.Combine(blocker, "logs"), new Clock(Noon));

        log.Write("goes nowhere");
        log.Trace("neither does this");
        log.Write(new Exception("or this"), "test");
    }

    [Fact]
    public void The_file_can_be_read_while_the_app_is_still_writing_to_it()
    {
        var log = new ActivityLog(LogDir, new Clock(Noon));
        log.Write("one");
        using var reader = new FileStream(log.TodaysFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

        log.Write("two"); // must not fail because someone has the file open

        using var text = new StreamReader(reader);
        Assert.Contains("two", text.ReadToEnd());
    }

    // ---- what the vATIS client and the engine report ----

    private sealed class ScriptedSocket(Func<string, string[]> reply) : IVatisSocketFactory
    {
        public Task<IVatisSocket> ConnectAsync(CancellationToken ct) => Task.FromResult<IVatisSocket>(new Socket(reply));

        private sealed class Socket(Func<string, string[]> reply) : IVatisSocket
        {
            private readonly Queue<string> _replies = new();
            public Task SendAsync(string text, CancellationToken ct)
            {
                foreach (var r in reply(text)) _replies.Enqueue(r);
                return Task.CompletedTask;
            }
            public Task<string?> ReceiveAsync(TimeSpan timeout, CancellationToken ct) =>
                Task.FromResult(_replies.Count > 0 ? _replies.Dequeue() : null);
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task The_vatis_client_reports_every_message_it_sends_and_receives()
    {
        var trace = new List<string>();
        var client = new VatisClient(new ScriptedSocket(_ => [
            """{"type":"somethingElse"}""",
            """{"type":"stations","stations":[{"id":"a1","name":"KSFO","atisType":"Combined","presets":["D-ATIS"]}]}""",
        ]), trace.Add);

        await client.GetStationsAsync();

        Assert.Contains(trace, t => t.StartsWith("-> vATIS ") && t.Contains("getStations"));
        Assert.Contains(trace, t => t.Contains("(not the reply asked for, skipped)") && t.Contains("somethingElse"));
        Assert.Contains(trace, t => t.StartsWith("<- vATIS ") && t.Contains("\"KSFO\""));
    }

    [Fact]
    public async Task The_vatis_client_reports_a_missing_reply_and_a_fire_and_forget_send()
    {
        var trace = new List<string>();
        var client = new VatisClient(new ScriptedSocket(_ => []), trace.Add);

        await client.ConfigureAtisAsync("a1", "ILS RWY 28R IN USE.", "TWY B CLSD.", syncAtisLetter: false);
        Assert.Contains(trace, t => t.Contains("no reply expected") && t.Contains("configureAtis") && t.Contains("ILS RWY 28R IN USE."));

        await Assert.ThrowsAsync<VatisUnavailableException>(() => client.GetAtisAsync("a1"));
        Assert.Contains(trace, t => t.Contains("no matching reply") || t.Contains("nothing more within"));
    }

    [Fact]
    public async Task The_client_works_without_a_trace()
    {
        var client = new VatisClient(new ScriptedSocket(_ => [
            """{"type":"stations","stations":[]}"""]));
        Assert.Empty(await client.GetStationsAsync());
    }
}
