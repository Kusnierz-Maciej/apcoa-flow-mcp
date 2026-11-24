using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ApcoaFlowApiClient.Converters;

/// <summary>
/// Serializes <see cref="DateTimeOffset"/> as ISO-8601 UTC (<c>yyyy-MM-ddTHH:mm:ss.fffZ</c>), the format
/// APCOA uses for most timestamps (sessions, offers, consumer). Reads any ISO-8601 the server returns.
/// </summary>
public class IsoDateTimeOffsetConverter : JsonConverter<DateTimeOffset?>
{
    private const string WriteFormat = "yyyy-MM-ddTHH:mm:ss.fffZ";

    public override DateTimeOffset? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
            return null;
        var s = reader.GetString();
        if (string.IsNullOrWhiteSpace(s))
            return null;
        return DateTimeOffset.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
    }

    public override void Write(Utf8JsonWriter writer, DateTimeOffset? value, JsonSerializerOptions options)
    {
        if (value is null)
            writer.WriteNullValue();
        else
            writer.WriteStringValue(value.Value.ToUniversalTime().ToString(WriteFormat, CultureInfo.InvariantCulture));
    }
}
