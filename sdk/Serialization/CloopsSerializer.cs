using System.Buffers;
using System.Text;
using NATS.Client.Core;

namespace CLOOPS.NATS.Serialization;

/// <summary>
/// Custom serializer registry that uses Util.cs serialization methods
/// </summary>
internal class CloopsSerializerRegistry : INatsSerializerRegistry
{
    public INatsSerialize<T> GetSerializer<T>()
    {
        return new CloopsSerializer<T>();
    }

    public INatsDeserialize<T> GetDeserializer<T>()
    {
        return new CloopsSerializer<T>();
    }
}

/// <summary>
/// Custom serializer implementation using Util.cs JsonSerializerOptions
/// Provides consistent JSON serialization with custom options across the application
/// Uses direct buffer operations for optimal performance
/// </summary>
internal class CloopsSerializer<T> : INatsSerialize<T>, INatsDeserialize<T>
{
    public void Serialize(IBufferWriter<byte> buffer, T value)
    {
        // Use System.Text.Json directly with custom options, writing to buffer
        using var jsonWriter = new System.Text.Json.Utf8JsonWriter(buffer);
        System.Text.Json.JsonSerializer.Serialize(jsonWriter, value, BaseNatsUtil.JsonSerializerOptions);
    }

    public T Deserialize(in ReadOnlySequence<byte> data)
    {
        // Convert ReadOnlySequence<byte> to byte[] for deserialization
        var bytes = data.ToArray();
        var type = typeof(T);

        // Handle special types that need direct byte manipulation (not JSON)
        if (type == typeof(byte[]))
        {
            return (T)(object)bytes;
        }
        else if (type == typeof(string))
        {
            // Since Serialize always produces JSON, we need to deserialize as JSON first
            // This handles JSON-encoded strings (with quotes) correctly
            // If JSON deserialization fails, fall back to UTF-8 decoding for raw strings
            try
            {
                var jsonString = System.Text.Json.JsonSerializer.Deserialize<string>(bytes, BaseNatsUtil.JsonSerializerOptions);
                if (jsonString != null)
                    return (T)(object)jsonString;
            }
            catch
            {
                // If JSON deserialization fails, treat as raw UTF-8 string
            }
            // Fallback to UTF-8 decoding for raw string payloads
            return (T)(object)Encoding.UTF8.GetString(bytes);
        }
        else if (type == typeof(void))
        {
            return default(T)!;
        }

        // For all other types (including primitives like int, long, bool, float, double),
        // use JSON deserialization since Serialize always produces JSON
        // JsonSerializer.Deserialize handles primitives correctly when they're JSON-encoded
        return System.Text.Json.JsonSerializer.Deserialize<T>(bytes, BaseNatsUtil.JsonSerializerOptions)
            ?? throw new InvalidOperationException($"Failed to deserialize to {type.Name}");
    }
}
