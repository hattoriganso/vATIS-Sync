using System.Text;

namespace VatisSync.Core;

/// <summary>
/// The record of what the app did, kept on disk so a problem can be looked into afterwards (the Log on the Overview
/// is cleared when you press Clear or close the app). One file per day in %APPDATA%\VatisSync\logs, named
/// <c>activity-2026-11-10.log</c>, one entry per line:
/// <code>2026-11-10 14:03:22.123  INFO   KSFO_A: update approved (...)</code>
/// <para>
/// Everything the Log panel shows goes here. <see cref="Trace"/> adds the detail needed to find out why something
/// misbehaved (every message sent to or received from vATIS, and why each station was written or held), and can be
/// switched off. Writing never throws and never waits on anything but the file: a log must not become the problem.
/// </para>
/// </summary>
public sealed class ActivityLog
{
    /// <summary>Past this size a day's file is moved aside (to <c>.old</c>, replacing an older one) and a fresh one started.</summary>
    public const long MaxBytes = 10_000_000;

    /// <summary>Files not written to for this many days are deleted.</summary>
    public const int KeepDays = 14;

    /// <summary>A single entry longer than this is cut, so one enormous message cannot fill the file.</summary>
    public const int MaxEntryChars = 20_000;

    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private DateOnly _prunedOn;
    private volatile bool _traceEnabled = true;

    public ActivityLog(string? directory = null, TimeProvider? time = null)
    {
        Directory = directory ?? DefaultDirectory;
        _time = time ?? TimeProvider.System;
    }

    public string Directory { get; }

    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VatisSync", "logs");

    /// <summary>Whether <see cref="Trace"/> writes anything. On by default: a first run needs the detail most.</summary>
    public bool TraceEnabled
    {
        get => _traceEnabled;
        set => _traceEnabled = value;
    }

    /// <summary>The file today's entries go to.</summary>
    public string TodaysFile => FileFor(DateOnly.FromDateTime(_time.GetLocalNow().DateTime));

    /// <summary>A line of the Log panel, with how serious it is.</summary>
    public void Write(string message, LogKind kind = LogKind.Info) => Append(Label(kind), message);

    /// <summary>Detail for finding a problem. Written only while <see cref="TraceEnabled"/> is on.</summary>
    public void Trace(string message)
    {
        if (_traceEnabled) Append("TRACE", message);
    }

    /// <summary>An error with its full details (type, message, stack).</summary>
    public void Write(Exception error, string source) => Append("ERROR", $"{source}: {error}");

    private static string Label(LogKind kind) => kind switch
    {
        LogKind.Success => "OK",
        LogKind.Warning => "WARN",
        LogKind.Error => "ERROR",
        _ => "INFO",
    };

    private string FileFor(DateOnly day) => Path.Combine(Directory, $"activity-{day:yyyy-MM-dd}.log");

    private void Append(string label, string message)
    {
        try
        {
            lock (_gate)
            {
                var now = _time.GetLocalNow();
                var day = DateOnly.FromDateTime(now.DateTime);
                var path = FileFor(day);

                System.IO.Directory.CreateDirectory(Directory);
                if (_prunedOn != day) { Prune(now); _prunedOn = day; }
                if (File.Exists(path) && new FileInfo(path).Length > MaxBytes)
                    File.Move(path, path + ".old", overwrite: true);

                if (message.Length > MaxEntryChars) message = message[..MaxEntryChars] + $" ...[cut, {message.Length} characters]";
                // A multi-line entry (a stack trace) keeps its lines under the first one, indented, so every entry
                // still starts with a timestamp.
                var text = message.Replace("\r\n", "\n").Replace("\n", "\n" + new string(' ', 32));
                var entry = $"{now:yyyy-MM-dd HH:mm:ss.fff}  {label,-5}  {text}{Environment.NewLine}";

                // Shared, so the file can be opened in an editor while the app is writing to it.
                using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                stream.Write(Encoding.UTF8.GetBytes(entry));
            }
        }
        catch
        {
            // Never let logging fail the app (disk full, folder locked, antivirus).
        }
    }

    /// <summary>Deletes day files nobody has written to for <see cref="KeepDays"/> days.</summary>
    private void Prune(DateTimeOffset now)
    {
        var cutoff = now.UtcDateTime.AddDays(-KeepDays);
        foreach (var file in System.IO.Directory.EnumerateFiles(Directory, "activity-*"))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(file) < cutoff) File.Delete(file);
            }
            catch
            {
                // In use, or already gone: leave it for next time.
            }
        }
    }
}
