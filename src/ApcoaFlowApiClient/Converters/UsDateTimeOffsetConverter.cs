using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ApcoaFlowApiClient.Converters;

/// <summary>
/// Serializes <see cref="DateTimeOffset"/> as <c>MM/dd/yyyy HH:mm</c> — the format the tariff breakdown
/// endpoint expects for its <c>entryTime</c>/<c>exitTime</c> query params. Values are treated as UTC.
/// </summary>
public class UsDateTimeOffsetConverter : JsonConverter<DateTimeOffset>
{
    private const string Format = "MM/dd/yyyy HH:mm";

    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var s = reader.GetString();
        return DateTimeOffset.ParseExact(s!, Format, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
    }

    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToUniversalTime().ToString(Format, CultureInfo.InvariantCulture));
}
