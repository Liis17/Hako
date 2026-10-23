using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hako.Core.Instances;

/// <summary>Matches Swift JSONEncoder.DateEncodingStrategy.iso8601 at whole-second UTC precision.</summary>
internal sealed class UtcWholeSecondDateTimeOffsetConverter : JsonConverter<DateTimeOffset>
{
    private const string Format = "yyyy-MM-dd'T'HH:mm:ss'Z'";

    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var text = reader.GetString() ?? throw new JsonException("A backup date cannot be null.");
        if (!DateTimeOffset.TryParseExact(text, Format, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var value))
            throw new JsonException("A backup date must be an ISO 8601 UTC timestamp with whole-second precision.");
        return value;
    }

    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.UtcDateTime.ToString(Format, CultureInfo.InvariantCulture));
}

internal sealed class UtcWholeSecondNullableDateTimeOffsetConverter : JsonConverter<DateTimeOffset?>
{
    private readonly UtcWholeSecondDateTimeOffsetConverter _converter = new();

    public override bool HandleNull => true;

    public override DateTimeOffset? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.Null ? null : _converter.Read(ref reader, typeof(DateTimeOffset), options);

    public override void Write(Utf8JsonWriter writer, DateTimeOffset? value, JsonSerializerOptions options)
    {
        if (value is null) writer.WriteNullValue();
        else _converter.Write(writer, value.Value, options);
    }
}
