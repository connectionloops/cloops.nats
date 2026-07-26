using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CLOOPS.NATS.Serialization;
using NATS.Client.Core;
using NATS.Client.JetStream;

namespace CLOOPS.NATS;
/// <summary>
/// Utility class various helper methods
/// </summary>
public class BaseNatsUtil
{
    /// <summary>
    /// Default JSON serializer options
    /// </summary>
    public static JsonSerializerOptions JsonSerializerOptions = new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        Converters =
        {
            new JsonStringEnumConverter(),
            new Int64StringJsonConverter(),
            new UInt64StringJsonConverter(),
        },
    };

    /// <summary>
    /// Serialize an object to a JSON string
    /// </summary>
    /// <typeparam name="T">The type of the object to serialize</typeparam>
    /// <param name="obj">The object to serialize</param>
    /// <returns>A JSON string representing the object, or null if the input object is null</returns>
    public static string? Serialize<T>(T? obj)
    {
        if (obj == null)
            return null;

        return JsonSerializer.Serialize(obj, typeof(T), JsonSerializerOptions);
    }

    /// <summary>
    /// Deserialize a JSON string to an object
    /// </summary>
    /// <typeparam name="T">The type of the object to deserialize</typeparam>
    /// <param name="json">The JSON string to deserialize</param>
    /// <returns>An object of type T</returns>
    public static T? Deserialize<T>(string json)
    {
        return JsonSerializer.Deserialize<T>(json, JsonSerializerOptions);
    }

    /// <summary>
    /// Creates a typed message wrapper that mimics NatsMsg&lt;T&gt; structure
    /// This approach avoids the complex constructor reflection issues
    /// </summary>
    /// <param name="originalMsg">The original NATS message</param>
    /// <param name="payloadType">The type of the payload</param>
    /// <returns>Typed NATS message instance</returns>
    internal static object CreateTypedMsgWrapper(NatsMsg<byte[]> originalMsg, Type payloadType)
    {
        var payload = Deserialize(originalMsg.Data, payloadType);

        return NatsMsgAccessor.GetFactory(payloadType)(
            originalMsg.Subject,
            originalMsg.ReplyTo,
            originalMsg.Size,
            originalMsg.Headers,
            payload,
            originalMsg.Connection,
            originalMsg.Flags);
    }

    /// <summary>
    /// Creates a typed message wrapper for JetStream messages
    /// </summary>
    /// <param name="originalMsg">The original JetStream NATS message</param>
    /// <param name="payloadType">The type of the payload</param>
    /// <returns>Typed NATS message instance</returns>
    internal static object CreateTypedMsgWrapper(NatsJSMsg<byte[]> originalMsg, Type payloadType)
    {
        var payload = Deserialize(originalMsg.Data, payloadType);

        return NatsMsgAccessor.GetFactory(payloadType)(
            originalMsg.Subject,
            originalMsg.ReplyTo,
            originalMsg.Size,
            originalMsg.Headers,
            payload,
            originalMsg.Connection,
            default);  // flags - JetStream doesn't have this
    }

    /// <summary>
    /// Creates a typed NATS message by preserving metadata from an existing typed wrapper and replacing its payload.
    /// </summary>
    /// <param name="originalMsg">The current typed NATS message.</param>
    /// <param name="payloadType">The payload type expected by the handler.</param>
    /// <param name="payload">The replacement payload.</param>
    /// <returns>A typed NATS message instance with the replacement payload.</returns>
    internal static object CreateTypedMsgWrapper(object originalMsg, Type payloadType, object? payload)
    {
        var metadata = NatsMsgAccessor.Read(originalMsg);

        return NatsMsgAccessor.GetFactory(payloadType)(
            metadata.Subject,
            metadata.ReplyTo,
            metadata.Size,
            metadata.Headers,
            payload,
            metadata.Connection,
            metadata.Flags);
    }

    internal static object? Deserialize(byte[]? data, Type payloadType)
    {
        object? payload = null;
        if (payloadType == typeof(string))
        {
            // Since data may be JSON-encoded (from CloopsSerializer), try JSON deserialization first
            // This handles JSON-encoded strings (with quotes) correctly
            // If JSON deserialization fails, fall back to UTF-8 decoding for raw strings
            try
            {
                var jsonString = JsonSerializer.Deserialize<string>(data ?? Array.Empty<byte>(), JsonSerializerOptions);
                if (jsonString != null)
                {
                    payload = jsonString;
                }
                else
                {
                    payload = Encoding.UTF8.GetString(data ?? Array.Empty<byte>());
                }
            }
            catch
            {
                // If JSON deserialization fails, treat as raw UTF-8 string
                payload = Encoding.UTF8.GetString(data ?? Array.Empty<byte>());
            }
        }
        else if (payloadType == typeof(int))
        {
            payload = BitConverter.ToInt32(data ?? Array.Empty<byte>());
        }
        else if (payloadType == typeof(long))
        {
            payload = BitConverter.ToInt64(data ?? Array.Empty<byte>());
        }
        else if (payloadType == typeof(float))
        {
            payload = BitConverter.ToSingle(data ?? Array.Empty<byte>());
        }
        else if (payloadType == typeof(double))
        {
            payload = BitConverter.ToDouble(data ?? Array.Empty<byte>());
        }
        else if (payloadType == typeof(bool))
        {
            payload = BitConverter.ToBoolean(data ?? Array.Empty<byte>());
        }
        else if (payloadType == typeof(byte[]))
        {
            payload = data;
        }
        else if (payloadType == typeof(void))
        {
            payload = null;
        }
        else
        {
            payload = JsonSerializer.Deserialize(data, payloadType, JsonSerializerOptions);
        }

        return payload;
    }
}