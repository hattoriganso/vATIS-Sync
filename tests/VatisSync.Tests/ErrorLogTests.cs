using VatisSync.Core;

namespace VatisSync.Tests;

public class ErrorLogTests : TempDirTests
{
    private string LogPath => Path.Combine(Dir, "logs", "errors.log");

    private static Exception Thrown(string message)
    {
        try { throw new InvalidOperationException(message); }
        catch (Exception ex) { return ex; }
    }

    [Fact]
    public void Writes_the_source_the_message_and_the_stack_trace_creating_folders()
    {
        var path = new ErrorLog(LogPath).Write(Thrown("boom"), "UI thread");

        Assert.Equal(LogPath, path);
        var text = File.ReadAllText(LogPath);
        Assert.Contains("[UI thread]", text);
        Assert.Contains("InvalidOperationException: boom", text);
        Assert.Contains("Thrown", text); // the stack trace names the throwing method
    }

    [Fact]
    public void Different_errors_are_appended_in_order()
    {
        var log = new ErrorLog(LogPath);
        log.Write(Thrown("first"), "a");
        log.Write(Thrown("second"), "b");

        var text = File.ReadAllText(LogPath);
        Assert.True(text.IndexOf("first", StringComparison.Ordinal) < text.IndexOf("second", StringComparison.Ordinal));
    }

    [Fact]
    public void The_same_error_repeating_within_the_window_is_written_once_then_again_later()
    {
        var time = new FakeTime();
        var log = new ErrorLog(LogPath, time);
        var error = Thrown("every frame");

        log.Write(error, "UI thread");
        time.Advance(TimeSpan.FromSeconds(1));
        log.Write(error, "UI thread");
        Assert.Single(File.ReadAllLines(LogPath), l => l.Contains("every frame"));

        time.Advance(ErrorLog.RepeatWindow);
        log.Write(error, "UI thread");
        Assert.Equal(2, File.ReadAllLines(LogPath).Count(l => l.Contains("every frame")));
    }

    [Fact]
    public void A_file_past_the_size_cap_is_moved_aside_and_a_fresh_one_started()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
        File.WriteAllText(LogPath, new string('x', (int)ErrorLog.MaxBytes + 10));

        new ErrorLog(LogPath).Write(Thrown("after rotation"), "t");

        Assert.True(File.Exists(LogPath + ".old"));
        Assert.True(new FileInfo(LogPath).Length < 10_000);
        Assert.Contains("after rotation", File.ReadAllText(LogPath));
    }

    [Fact]
    public void An_unwritable_location_returns_null_instead_of_throwing()
    {
        File.WriteAllText(Path.Combine(Dir, "blocker"), "a file where a folder is needed");
        var path = new ErrorLog(Path.Combine(Dir, "blocker", "errors.log")).Write(Thrown("x"), "t");
        Assert.Null(path);
    }

    private sealed class FakeTime : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 11, 7, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }
}
