using System.Text.Json;
using System.Text.Json.Serialization;

namespace VatisSync.Core;

/// <summary>Which corner of the screen the review strip appears in.</summary>
[JsonConverter(typeof(ScreenCornerConverter))]
public enum ScreenCorner
{
    BottomRight,
    BottomLeft,
    TopRight,
    TopLeft,
}

/// <summary>
/// Reads the corner from settings.json. A value it does not know means the default corner instead of an error, so it can
/// never cost the user the rest of their settings.
/// </summary>
public sealed class ScreenCornerConverter : JsonConverter<ScreenCorner>
{
    public override ScreenCorner Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String && Enum.TryParse<ScreenCorner>(reader.GetString(), ignoreCase: true, out var value)
            && Enum.IsDefined(value))
            return value;
        return ScreenCorner.BottomRight;
    }

    public override void Write(Utf8JsonWriter writer, ScreenCorner value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString());
}
