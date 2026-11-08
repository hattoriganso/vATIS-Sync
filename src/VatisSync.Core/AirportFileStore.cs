using System.Text;
using System.Text.RegularExpressions;
using Tomlyn;
using Tomlyn.Model;

namespace VatisSync.Core;

/// <summary>
/// One settings file per airport, <c>%APPDATA%\VatisSync\Airports\KSFO.toml</c>, so an airport's own rules and frequencies are
/// kept between sessions whether or not the airport is in the vATIS profile that is open. An airport that follows the defaults
/// everywhere has no file. Files are TOML so they can carry comments and be edited by hand while the app is closed.
/// </summary>
public sealed class AirportFileStore(string? folder = null)
{
    private static readonly Regex IcaoPattern = new("^[A-Z0-9]{4}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public string Folder { get; } = folder ?? DefaultFolder;

    public static string DefaultFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VatisSync", "Airports");

    /// <summary>Raised when a file cannot be read or written. The text is ready to show the user.</summary>
    public event Action<string>? Warning;

    public static bool IsIcao(string? icao) => icao is not null && IcaoPattern.IsMatch(icao.ToUpperInvariant());

    /// <summary>The file for this airport (it may not exist).</summary>
    public string PathFor(string icao) => Path.Combine(Folder, icao.ToUpperInvariant() + ".toml");

    /// <summary>The airport's own settings, or null if it has no file (it follows the defaults) or the file is unusable.</summary>
    public AirportSettings? Load(string icao)
    {
        if (!IsIcao(icao)) return null;
        var path = PathFor(icao);
        if (!File.Exists(path)) return null;
        return TryRead(icao.ToUpperInvariant(), path);
    }

    /// <summary>Every airport that has a usable file, by ICAO.</summary>
    public IReadOnlyDictionary<string, AirportSettings> LoadAll()
    {
        var result = new Dictionary<string, AirportSettings>();
        if (!Directory.Exists(Folder)) return result;
        try
        {
            foreach (var file in Directory.EnumerateFiles(Folder, "*.toml"))
            {
                var icao = Path.GetFileNameWithoutExtension(file).ToUpperInvariant();
                if (!IsIcao(icao)) continue;
                if (TryRead(icao, file) is { } settings) result[icao] = settings;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Warning?.Invoke(Messages.Get(Msg.AirportsFolderFailed, new { path = Folder, reason = ex.Message }));
        }
        return result;
    }

    /// <summary>
    /// Writes the airport's settings, or removes its file when it follows the defaults everywhere (nothing to keep).
    /// Returns false if that failed.
    /// </summary>
    public bool Save(string icao, AirportSettings settings)
    {
        if (!IsIcao(icao)) return false;
        icao = icao.ToUpperInvariant();
        var path = PathFor(icao);
        try
        {
            var cleaned = settings.Cleaned();
            if (cleaned.IsDefault)
            {
                if (File.Exists(path)) File.Delete(path);
                return true;
            }

            Directory.CreateDirectory(Folder);
            var temp = path + ".tmp";
            File.WriteAllText(temp, ToToml(icao, cleaned), new UTF8Encoding(false));
            File.Move(temp, path, overwrite: true); // never leaves a half-written file behind
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Warning?.Invoke(Messages.Get(Msg.AirportsSaveFailed, new { icao, path, reason = ex.Message }));
            return false;
        }
    }

    /// <summary>Writes a file for each airport that does not have one yet (used to bring over settings from an older version).</summary>
    public int ImportMissing(IEnumerable<KeyValuePair<string, AirportSettings>> airports)
    {
        var imported = 0;
        foreach (var (icao, settings) in airports)
        {
            if (!IsIcao(icao) || File.Exists(PathFor(icao)) || settings.Cleaned().IsDefault) continue;
            if (Save(icao, settings)) imported++;
        }
        return imported;
    }

    // ---- reading ----

    private AirportSettings? TryRead(string icao, string path)
    {
        try
        {
            return Parse(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is TomlException or InvalidDataException)
        {
            // Keep what the user wrote, out of the way, so the next save cannot destroy it.
            var backup = path + ".broken";
            try { File.Move(path, backup, overwrite: true); }
            catch (Exception move) when (move is IOException or UnauthorizedAccessException) { backup = path; }
            Warning?.Invoke(Messages.Get(Msg.AirportsReadFailed, new { icao, path = backup, reason = ex.Message }));
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Warning?.Invoke(Messages.Get(Msg.AirportsReadFailed, new { icao, path, reason = ex.Message }));
            return null;
        }
    }

    /// <summary>Reads the text of an airport file. Unknown keys are ignored; values that make no sense are skipped.</summary>
    public static AirportSettings Parse(string toml)
    {
        var table = TomlSerializer.Deserialize<TomlTable>(toml) ?? [];

        var rules = new List<string>();
        if (table.TryGetValue("disabled-rules", out var r) && r is TomlArray array)
            rules.AddRange(array.OfType<string>().Where(s => !string.IsNullOrWhiteSpace(s)));

        var frequencies = new Dictionary<FrequencyRole, FrequencySetting>();
        if (table.TryGetValue("frequencies", out var f) && f is TomlTable positions)
        {
            foreach (var (name, value) in positions)
            {
                if (!Enum.TryParse<FrequencyRole>(name, ignoreCase: true, out var role) || value is not TomlTable entry) continue;
                if (!entry.TryGetValue("mode", out var m) || m is not string modeText ||
                    !Enum.TryParse<FrequencyMode>(modeText, ignoreCase: true, out var mode)) continue;
                var frequency = entry.TryGetValue("frequency", out var q) && q is string text ? text.Trim() : null;
                frequencies[role] = new FrequencySetting(mode, frequency);
            }
        }

        return new AirportSettings
        {
            UseDefaultRules = !(table.TryGetValue("use-default-rules", out var ur) && ur is false),
            DisabledRules = rules,
            UseDefaultFrequencies = !(table.TryGetValue("use-default-frequencies", out var uf) && uf is false),
            Frequencies = frequencies,
        };
    }

    // ---- writing ----

    /// <summary>The text of an airport file: short comments explaining it, then the settings.</summary>
    public static string ToToml(string icao, AirportSettings settings)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# vATIS-Sync settings for {icao.ToUpperInvariant()} only.");
        sb.AppendLine("# The app writes this file whenever you change this airport's settings. You can edit it while the app is");
        sb.AppendLine("# closed. Comments you add are not kept the next time the app saves it.");
        sb.AppendLine();
        sb.AppendLine("# true: this airport follows the default rules chosen in Settings. false: it uses the list below.");
        sb.AppendLine($"use-default-rules = {Bool(settings.UseDefaultRules)}");
        sb.AppendLine("# Built-in rules switched OFF for this airport (only used when use-default-rules is false).");
        sb.AppendLine("# Rule names: " + string.Join(", ", BuiltInRules.All.Select(r => r.Id)));
        sb.AppendLine($"disabled-rules = [{string.Join(", ", settings.DisabledRules.Select(Quote))}]");
        sb.AppendLine();
        sb.AppendLine("# true: this airport follows the default frequency settings chosen in Settings. false: it uses the table below.");
        sb.AppendLine($"use-default-frequencies = {Bool(settings.UseDefaultFrequencies)}");
        sb.AppendLine("# One line per position. mode is \"Remove\" or \"Override\"; frequency is only used with \"Override\".");
        sb.AppendLine("# Positions left out keep the real-world text. Positions: " + string.Join(", ", Enum.GetNames<FrequencyRole>()));
        sb.AppendLine("[frequencies]");
        foreach (var (role, setting) in settings.Frequencies.OrderBy(kv => kv.Key))
        {
            if (setting.Mode == FrequencyMode.Off) continue;
            sb.Append($"{role} = {{ mode = {Quote(setting.Mode.ToString())}");
            if (setting.Mode == FrequencyMode.Override && !string.IsNullOrWhiteSpace(setting.Frequency))
                sb.Append($", frequency = {Quote(setting.Frequency!)}");
            sb.AppendLine(" }");
        }
        return sb.ToString();
    }

    private static string Bool(bool value) => value ? "true" : "false";

    private static string Quote(string value) =>
        "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r") + "\"";
}
