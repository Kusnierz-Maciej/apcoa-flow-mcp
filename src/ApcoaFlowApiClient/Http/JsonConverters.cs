using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ApcoaFlowApiClient.Http;

/// <summary>
/// Reads a value the API sends as a number in one endpoint and a string in another (<c>tariffCode</c>: int in
/// <c>spaces/{id}</c> and <c>sessions</c>, string in <c>search</c>) as a string.
/// </summary>
public sealed class StringOrNumberConverter : JsonConverter<string?>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType switch
        {
            JsonTokenType.Null => null,
            JsonTokenType.String => reader.GetString(),
            JsonTokenType.Number => reader.TryGetInt64(out var l)
                ? l.ToString(CultureInfo.InvariantCulture)
                : reader.GetDouble().ToString(CultureInfo.InvariantCulture),
            _ => throw new JsonException($"Expected a string or number, got {reader.TokenType}."),
        };

    public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options)
    {
        if (value is null) writer.WriteNullValue();
        else writer.WriteStringValue(value);
    }
}

/// <summary>Writes dates the way the web app does: UTC with milliseconds, <c>2026-11-27T14:33:00.000Z</c>.</summary>
public sealed class UtcMillisecondsConverter : JsonConverter<DateTimeOffset>
{
    public const string Format = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";

    public static string ToWire(DateTimeOffset value) => value.UtcDateTime.ToString(Format, CultureInfo.InvariantCulture);

    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.GetDateTimeOffset();

    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
        writer.WriteStringValue(ToWire(value));
}
