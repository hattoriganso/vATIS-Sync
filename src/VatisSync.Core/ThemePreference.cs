using System.Text.Json;
using System.Text.Json.Serialization;

namespace VatisSync.Core;

/// <summary>What the user chose for the app's colors.</summary>
[JsonConverter(typeof(ThemePreferenceConverter))]
public enum ThemePreference
{
    /// <summary>Follow Windows' light or dark app mode, and change with it. The default.</summary>
    System,

    Light,
    Dark,
}

/// <summary>The color scheme that is actually shown.</summary>
public enum ThemeMode
{
    Light,
    Dark,

    /// <summary>Windows High Contrast: the colors come from the user's Windows settings.</summary>
    HighContrast,
}

public static class ThemeRules
{
    /// <summary>Windows High Contrast always wins, since the person turned it on for a reason; then the choice; then Windows.</summary>
    public static ThemeMode Resolve(ThemePreference preference, bool windowsIsDark, bool highContrast) =>
        highContrast ? ThemeMode.HighContrast
        : preference switch
        {
            ThemePreference.Light => ThemeMode.Light,
            ThemePreference.Dark => ThemeMode.Dark,
            _ => windowsIsDark ? ThemeMode.Dark : ThemeMode.Light,
        };
}

/// <summary>
/// Reads the preference from settings.json. A value it does not know (a typo, or one from a newer version) means
/// "follow Windows" instead of an error, so it can never cost the user the rest of their settings.
/// </summary>
public sealed class ThemePreferenceConverter : JsonConverter<ThemePreference>
{
    public override ThemePreference Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String && Enum.TryParse<ThemePreference>(reader.GetString(), ignoreCase: true, out var value)
            && Enum.IsDefined(value))
            return value;
        return ThemePreference.System;
    }

    public override void Write(Utf8JsonWriter writer, ThemePreference value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString());
}
