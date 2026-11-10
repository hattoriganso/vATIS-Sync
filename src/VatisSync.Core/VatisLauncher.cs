using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace VatisSync.Core;

/// <summary>Starts vATIS if it is not running, after making sure it will fetch the real ATIS letter.</summary>
public sealed class VatisLauncher : IVatisLauncher
{
    private readonly string _vatisDir;

    /// <param name="vatisDir">Defaults to %LOCALAPPDATA%\org.vatsim.vatis.</param>
    public VatisLauncher(string? vatisDir = null) =>
        _vatisDir = vatisDir ?? Path.Combine(LocalAppData(), "org.vatsim.vatis");

    private static string LocalAppData() =>
        Environment.GetEnvironmentVariable("LOCALAPPDATA") is { Length: > 0 } fromEnvironment
            ? fromEnvironment
            : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    public string ConfigPath => Path.Combine(_vatisDir, "AppConfig.json");
    public string ExePath => Path.Combine(_vatisDir, "current", "vATIS.exe");

    public LaunchResult EnsureRunning()
    {
        // Only ever a full path: a relative one would be looked up in whatever folder the app was started from.
        if (!Path.IsPathFullyQualified(ExePath)) return LaunchResult.NotFound;

        EnableAutoFetchAtisLetter(ConfigPath);

        if (Process.GetProcessesByName("vATIS").Length > 0) return LaunchResult.AlreadyRunning;
        if (!File.Exists(ExePath)) return LaunchResult.NotFound;

        try
        {
            Process.Start(new ProcessStartInfo(ExePath) { UseShellExecute = true });
            return LaunchResult.Started;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return LaunchResult.NotFound;
        }
    }

    /// <summary>
    /// Sets <c>autoFetchAtisLetter</c> to true if the key exists (as the original script does).
    /// Returns true if the file was changed. Never throws; a missing or unreadable file is left alone.
    /// </summary>
    public static bool EnableAutoFetchAtisLetter(string configPath)
    {
        var temp = configPath + ".vatis-sync.tmp";
        try
        {
            if (!File.Exists(configPath)) return false;
            if (JsonNode.Parse(File.ReadAllText(configPath)) is not JsonObject config) return false;
            if (config["autoFetchAtisLetter"] is not JsonValue current || current.GetValueKind() == JsonValueKind.True)
                return false;

            config["autoFetchAtisLetter"] = true;
            // Through a temporary file, so vATIS's settings are never left half-written.
            File.WriteAllText(temp, config.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, configPath, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            try { File.Delete(temp); }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { }
            return false;
        }
    }
}
