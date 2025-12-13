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
        // from sdk standpoint, we always consume the data as a byte[]
        // when we pass to handler function that's when we deserialize it
        return (T)(object)data.ToArray();
    }
}
