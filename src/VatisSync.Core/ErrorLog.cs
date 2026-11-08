using System.Text;

namespace VatisSync.Core;

/// <summary>
/// Appends unexpected errors, with their full stack traces, to a text file so they can be read after the fact.
/// Never throws: logging a failure must not become a second failure.
/// </summary>
public sealed class ErrorLog(string? path = null, TimeProvider? time = null)
{
    /// <summary>Past this size the file is moved aside (to <c>.old</c>) and a fresh one started.</summary>
    public const long MaxBytes = 1_000_000;

    /// <summary>An identical error repeated within this window is written once, so a failure that recurs every frame cannot flood the disk.</summary>
    public static readonly TimeSpan RepeatWindow = TimeSpan.FromSeconds(10);

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly object _gate = new();
    private string? _lastText;
    private DateTimeOffset _lastAt;

    public string FilePath { get; } = path ?? DefaultPath;

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VatisSync", "logs", "errors.log");

    /// <summary>Records the error. Returns the file it went to, or null if it could not be written.</summary>
    /// <param name="source">Where it was caught, for example "UI thread" or "background task".</param>
    public string? Write(Exception error, string source)
    {
        try
        {
            lock (_gate)
            {
                var now = _time.GetUtcNow();
                var text = error.ToString();
                if (text == _lastText && now - _lastAt < RepeatWindow) return FilePath;
                _lastText = text;
                _lastAt = now;

                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                if (File.Exists(FilePath) && new FileInfo(FilePath).Length > MaxBytes)
                    File.Move(FilePath, FilePath + ".old", overwrite: true);

                var entry = new StringBuilder()
                    .Append(now.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"))
                    .Append("  [").Append(source).AppendLine("]")
                    .AppendLine(text)
                    .AppendLine();
                File.AppendAllText(FilePath, entry.ToString());
                return FilePath;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }
}
