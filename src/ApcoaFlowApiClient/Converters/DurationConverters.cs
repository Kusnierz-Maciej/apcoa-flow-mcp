using System.Text.Json;
using System.Text.Json.Serialization;

namespace ApcoaFlowApiClient.Converters;

/// <summary>Serializes a <see cref="TimeSpan"/> as an integer number of minutes (APCOA durations).</summary>
public class MinutesTimeSpanConverter : JsonConverter<TimeSpan>
{
    public override TimeSpan Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        TimeSpan.FromMinutes(reader.TokenType == JsonTokenType.Number ? reader.GetInt32() : int.Parse(reader.GetString()!));

    public override void Write(Utf8JsonWriter writer, TimeSpan value, JsonSerializerOptions options) =>
        writer.WriteNumberValue((int)value.TotalMinutes);
}

/// <summary>Serializes a nullable <see cref="TimeSpan"/> as an integer number of minutes, or null.</summary>
public class NullableMinutesTimeSpanConverter : JsonConverter<TimeSpan?>
{
    public override TimeSpan? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.Null ? null : TimeSpan.FromMinutes(reader.GetInt32());

    public override void Write(Utf8JsonWriter writer, TimeSpan? value, JsonSerializerOptions options)
    {
        if (value is null) writer.WriteNullValue();
        else writer.WriteNumberValue((int)value.Value.TotalMinutes);
    }
}

/// <summary>
/// Serializes a nullable <see cref="TimeSpan"/> as a STRING number of minutes — APCOA's offer
/// <c>concessionValue</c> is a string like "120". Null/empty/non-numeric reads as null.
/// </summary>
public class StringMinutesTimeSpanConverter : JsonConverter<TimeSpan?>
{
    public override TimeSpan? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null) return null;
        var raw = reader.TokenType == JsonTokenType.Number ? reader.GetInt32().ToString() : reader.GetString();
        return int.TryParse(raw, out var m) ? TimeSpan.FromMinutes(m) : null;
    }

    public override void Write(Utf8JsonWriter writer, TimeSpan? value, JsonSerializerOptions options)
    {
        if (value is null) writer.WriteNullValue();
        else writer.WriteStringValue(((int)value.Value.TotalMinutes).ToString());
    }
}

/// <summary>Serializes a <see cref="TimeSpan"/> as an integer number of milliseconds (APCOA token TTL).</summary>
public class MillisecondsTimeSpanConverter : JsonConverter<TimeSpan>
{
    public override TimeSpan Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        TimeSpan.FromMilliseconds(reader.GetInt64());

    public override void Write(Utf8JsonWriter writer, TimeSpan value, JsonSerializerOptions options) =>
        writer.WriteNumberValue((long)value.TotalMilliseconds);
}

/// <summary>Milliseconds → <see cref="TimeSpan"/>?; tolerates a JSON null (e.g. the failed-login body sends ttl:null).</summary>
public class NullableMillisecondsTimeSpanConverter : JsonConverter<TimeSpan?>
{
    public override TimeSpan? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.Null ? null : TimeSpan.FromMilliseconds(reader.GetInt64());

    public override void Write(Utf8JsonWriter writer, TimeSpan? value, JsonSerializerOptions options)
    {
        if (value is { } v) writer.WriteNumberValue((long)v.TotalMilliseconds);
        else writer.WriteNullValue();
    }
}
