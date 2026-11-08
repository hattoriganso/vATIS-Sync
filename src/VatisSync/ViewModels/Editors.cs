using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using VatisSync.Core;

namespace VatisSync.ViewModels;

public sealed partial class RuleToggle(string id, string name, string description, bool enabled, Action<RuleToggle> changed)
    : ObservableObject
{
    public string Id { get; } = id;
    public string Name { get; } = name;
    public string Description { get; } = description;

    [ObservableProperty] private bool _enabled = enabled;

    partial void OnEnabledChanged(bool value) => changed(this);
}

public sealed record FrequencyModeChoice(FrequencyMode Mode, string Label);

/// <summary>One position's frequency handling: keep, remove, or replace with a frequency the user types.</summary>
public sealed partial class FrequencyRoleSetting : ObservableObject
{
    private readonly Action<FrequencyRoleSetting> _changed;

    public FrequencyRoleSetting(FrequencyRole role, string name, string example, FrequencySetting? setting,
        Action<FrequencyRoleSetting> changed)
    {
        Role = role;
        Name = name;
        Example = example;
        _mode = setting?.Mode ?? FrequencyMode.Off;
        _frequency = setting?.Frequency ?? "";
        _changed = changed;
    }

    public FrequencyRole Role { get; }
    public string Name { get; }
    public string Example { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOverride), nameof(FrequencyProblem))]
    private FrequencyMode _mode;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FrequencyProblem))]
    private string _frequency;

    public bool IsOverride => Mode == FrequencyMode.Override;

    /// <summary>Shown beside the box while the frequency is missing or not a valid one (such as 121.900).</summary>
    public string FrequencyProblem => IsOverride && !FrequencyDetector.IsValidFrequency(Frequency?.Trim())
        ? "Type a frequency such as 121.900. Until then the real text is kept."
        : "";

    public FrequencySetting ToSetting() => new(Mode, Mode == FrequencyMode.Override ? Frequency.Trim() : null);

    partial void OnModeChanged(FrequencyMode value) => _changed(this);
    partial void OnFrequencyChanged(string value) => _changed(this);
}

/// <summary>The built-in rule switches for one scope: the defaults, or one airport's own copy.</summary>
public sealed class RuleSetEditor
{
    private readonly Action _changed;

    public RuleSetEditor(IEnumerable<string> disabled, Action changed)
    {
        _changed = changed;
        Load(disabled);
    }

    public ObservableCollection<RuleToggle> Rules { get; } = [];

    /// <summary>Replaces the switches without reporting a change (the caller decides what to save).</summary>
    public void Load(IEnumerable<string> disabled)
    {
        var off = disabled.ToHashSet(StringComparer.OrdinalIgnoreCase);
        Rules.Clear();
        foreach (var r in BuiltInRules.All)
            Rules.Add(new RuleToggle(r.Id, r.Name, r.Description, !off.Contains(r.Id), _ => _changed()));
    }

    public List<string> Disabled => Rules.Where(r => !r.Enabled).Select(r => r.Id).ToList();
}

/// <summary>The frequency rows for one scope: the defaults, or one airport's own copy.</summary>
public sealed class FrequencySetEditor
{
    private readonly Action _changed;

    public static IReadOnlyList<FrequencyModeChoice> ModeChoices { get; } =
    [
        new(FrequencyMode.Off, "Keep as is"),
        new(FrequencyMode.Remove, "Remove"),
        new(FrequencyMode.Override, "Replace with"),
    ];

    private static readonly (FrequencyRole Role, string Name, string Example)[] Roles =
    [
        (FrequencyRole.ClearanceDelivery, "Clearance delivery", "CTC CD ON 121.7"),
        (FrequencyRole.Ground, "Ground", "GROUND CONTROL ON 121.9"),
        (FrequencyRole.Tower, "Tower", "TWR FREQ 118.5"),
        (FrequencyRole.Departure, "Departure", "DEPT FREQ IS 119.35"),
        (FrequencyRole.Approach, "Approach", "ALL ARRS CTC APCH 124.0"),
        (FrequencyRole.Ramp, "Ramp", "RAMP CONTROL OPEN 126.225"),
        (FrequencyRole.Metering, "Metering", "CONTACT GND METERING ON 121.67"),
        (FrequencyRole.Helicopter, "Helicopter control", "HELICOPTOR CONTROL OPEN ON 119.9"),
        (FrequencyRole.Other, "Other (no position named)", "RWY 9 FREQ 123.9"),
    ];

    public FrequencySetEditor(IReadOnlyDictionary<FrequencyRole, FrequencySetting> settings, Action changed)
    {
        _changed = changed;
        Load(settings);
    }

    public ObservableCollection<FrequencyRoleSetting> Rows { get; } = [];

    /// <summary>Replaces the rows without reporting a change (the caller decides what to save).</summary>
    public void Load(IReadOnlyDictionary<FrequencyRole, FrequencySetting> settings)
    {
        Rows.Clear();
        foreach (var (role, name, example) in Roles)
            Rows.Add(new FrequencyRoleSetting(role, name, example, settings.GetValueOrDefault(role), _ => _changed()));
    }

    public Dictionary<FrequencyRole, FrequencySetting> ToDictionary() =>
        Rows.Where(r => r.Mode != FrequencyMode.Off).ToDictionary(r => r.Role, r => r.ToSetting());
}
