using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CLOOPS.NATS.Serialization;

/// <summary>
/// Serializes <see cref="long"/> values as JSON strings and reads from both string and numeric tokens.
/// </summary>
public sealed class Int64StringJsonConverter : JsonConverter<long>
{
    /// <inheritdoc />
    public override long Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.TokenType == JsonTokenType.String
            ? long.Parse(reader.GetString()!, CultureInfo.InvariantCulture)
            : reader.GetInt64();

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, long value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.ToString(CultureInfo.InvariantCulture));
}

/// <summary>
/// Serializes <see cref="ulong"/> values as JSON strings and reads from both string and numeric tokens.
/// </summary>
public sealed class UInt64StringJsonConverter : JsonConverter<ulong>
{
    /// <inheritdoc />
    public override ulong Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.TokenType == JsonTokenType.String
            ? ulong.Parse(reader.GetString()!, CultureInfo.InvariantCulture)
            : reader.GetUInt64();

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, ulong value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.ToString(CultureInfo.InvariantCulture));
}
