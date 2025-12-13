using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
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
        Converters = { new JsonStringEnumConverter() },
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
        // Create NatsMsg<T> using the most basic approach that works
        // Use dynamic to avoid reflection constructor issues
        var msgType = typeof(NatsMsg<>).MakeGenericType(payloadType);
        var payload = Deserialize(originalMsg.Data, payloadType);

        return Activator.CreateInstance(msgType, new object?[] {
                originalMsg.Subject,        // subject
                originalMsg.ReplyTo,        // replyTo
                originalMsg.Size,           // size
                originalMsg.Headers,        // headers
                payload,                    // data (T)
                originalMsg.Connection,     // connection
                originalMsg.Flags           // flags
            }) ?? throw new InvalidOperationException($"Failed to create NatsMsg<{payloadType.Name}>");
    }

    /// <summary>
    /// Creates a typed message wrapper for JetStream messages
    /// </summary>
    /// <param name="originalMsg">The original JetStream NATS message</param>
    /// <param name="payloadType">The type of the payload</param>
    /// <returns>Typed NATS message instance</returns>
    internal static object CreateTypedMsgWrapper(NatsJSMsg<byte[]> originalMsg, Type payloadType)
    {
        // Create NatsMsg<T> from JetStream message
        var msgType = typeof(NatsMsg<>).MakeGenericType(payloadType);
        var payload = Deserialize(originalMsg.Data, payloadType);

        return Activator.CreateInstance(msgType, new object?[] {
                originalMsg.Subject,        // subject
                originalMsg.ReplyTo,        // replyTo
                originalMsg.Size,           // size
                originalMsg.Headers,        // headers
                payload,                    // data (T)
                originalMsg.Connection,     // connection
                default(NatsMsgFlags)       // flags - JetStream doesn't have this
            }) ?? throw new InvalidOperationException($"Failed to create NatsMsg<{payloadType.Name}> from JetStream message");
    }

    internal static object? Deserialize(byte[]? data, Type payloadType)
    {
        object? payload = null;
        if (payloadType == typeof(string))
        {
            payload = Encoding.UTF8.GetString(data ?? Array.Empty<byte>());
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