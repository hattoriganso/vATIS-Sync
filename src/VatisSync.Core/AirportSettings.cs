namespace VatisSync.Core;

/// <summary>The rules and frequency handling that apply to one airport, once defaults have been resolved.</summary>
public sealed record AirportRules(
    IReadOnlyList<string> DisabledRules,
    IReadOnlyDictionary<FrequencyRole, FrequencySetting> Frequencies);

/// <summary>
/// One airport's own choices. Each section either follows the defaults in <see cref="AppSettings"/> or is customized;
/// a customized section is a complete copy, not a set of per-item exceptions.
/// </summary>
public sealed record AirportSettings
{
    public bool UseDefaultRules { get; init; } = true;

    /// <summary>Built-in rule ids switched off for this airport. Only used when <see cref="UseDefaultRules"/> is false.</summary>
    public List<string> DisabledRules { get; init; } = [];

    public bool UseDefaultFrequencies { get; init; } = true;

    /// <summary>Only used when <see cref="UseDefaultFrequencies"/> is false.</summary>
    public Dictionary<FrequencyRole, FrequencySetting> Frequencies { get; init; } = [];

    /// <summary>Nothing customized, so there is nothing worth storing.</summary>
    public bool IsDefault => UseDefaultRules && UseDefaultFrequencies;

    /// <summary>The effective rules for this airport given the app-wide defaults.</summary>
    public AirportRules Resolve(AppSettings defaults) => new(
        UseDefaultRules ? defaults.DisabledRules : DisabledRules,
        (UseDefaultFrequencies ? defaults.Frequencies : Frequencies)
            .ToDictionary(kv => kv.Key, kv => FrequencyDetector.Usable(kv.Value)));

    internal AirportSettings Cleaned() => this with
    {
        DisabledRules = (DisabledRules ?? []).Where(r => !string.IsNullOrWhiteSpace(r)).Distinct().ToList(),
        Frequencies = AppSettings.CleanFrequencies(Frequencies),
    };
}
