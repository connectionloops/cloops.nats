using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;
using NATS.Client.Core;

namespace CLOOPS.NATS;

/// <summary>
/// Compiled, cached accessors for the open generic <see cref="NatsMsg{T}"/> wrapper.
/// </summary>
/// <remarks>
/// The consumer pipeline builds and inspects <see cref="NatsMsg{T}"/> instances for a payload type
/// that is only known at runtime. Doing that through <see cref="Activator.CreateInstance(Type, object[])"/>
/// and <see cref="Type.GetProperty(string)"/> re-resolves the constructor binding and re-runs a
/// name based member lookup on every message. These caches resolve the binding once per payload
/// type and reuse a compiled delegate afterwards.
/// </remarks>
internal static class NatsMsgAccessor
{
    /// <summary>
    /// Constructs a boxed <c>NatsMsg&lt;T&gt;</c> for a payload type resolved at runtime.
    /// </summary>
    internal delegate object Factory(
        string subject,
        string? replyTo,
        int size,
        NatsHeaders? headers,
        object? data,
        INatsConnection? connection,
        NatsMsgFlags flags);

    /// <summary>
    /// Reads the transport metadata of a boxed <c>NatsMsg&lt;T&gt;</c> without unboxing to a known T.
    /// </summary>
    internal delegate NatsMsgMetadata Reader(object message);

    private static readonly ConcurrentDictionary<Type, Factory> Factories = new();
    private static readonly ConcurrentDictionary<Type, Reader> Readers = new();
    private static readonly ConcurrentDictionary<Type, Type> MessageTypes = new();

    /// <summary>
    /// Gets the closed <c>NatsMsg&lt;payloadType&gt;</c> type.
    /// </summary>
    /// <param name="payloadType">The handler payload type.</param>
    /// <returns>The constructed generic message type.</returns>
    internal static Type GetMessageType(Type payloadType)
        => MessageTypes.GetOrAdd(payloadType, static t => typeof(NatsMsg<>).MakeGenericType(t));

    /// <summary>
    /// Gets the cached factory for <c>NatsMsg&lt;payloadType&gt;</c>.
    /// </summary>
    /// <param name="payloadType">The handler payload type.</param>
    /// <returns>A compiled constructor delegate.</returns>
    internal static Factory GetFactory(Type payloadType)
        => Factories.GetOrAdd(payloadType, static t => BuildFactory(t));

    /// <summary>
    /// Reads the transport metadata from an existing typed message wrapper.
    /// </summary>
    /// <param name="message">A boxed <c>NatsMsg&lt;T&gt;</c>.</param>
    /// <returns>The subject, reply subject, size, headers, connection, and flags.</returns>
    internal static NatsMsgMetadata Read(object message)
        => Readers.GetOrAdd(message.GetType(), static t => BuildReader(t))(message);

    /// <summary>
    /// Validates that a message is a <c>NatsMsg&lt;payloadType&gt;</c>.
    /// </summary>
    /// <param name="message">The message to check.</param>
    /// <param name="payloadType">The expected payload type.</param>
    /// <param name="parameterName">The name of the argument being validated.</param>
    /// <exception cref="ArgumentException">Thrown when the message is not the expected wrapper type.</exception>
    /// <remarks>
    /// Called from the public context constructors so that a mismatched message fails immediately
    /// with a clear message rather than later, inside a property getter.
    /// </remarks>
    internal static void EnsureTypedMessage(object message, Type payloadType, string parameterName)
    {
        if (!GetMessageType(payloadType).IsInstanceOfType(message))
        {
            throw new ArgumentException(
                $"Message must be a NatsMsg<{payloadType.Name}> to match the declared payload type, but was {message.GetType().Name}.",
                parameterName);
        }
    }

    /// <summary>
    /// Compiles a constructor delegate for <c>NatsMsg&lt;payloadType&gt;</c>.
    /// </summary>
    /// <remarks>
    /// Constructor arguments are bound by parameter name rather than position so that a reordering
    /// in a future NATS.Net release fails loudly at startup instead of silently mis-assigning fields.
    /// </remarks>
    private static Factory BuildFactory(Type payloadType)
    {
        var msgType = GetMessageType(payloadType);
        var ctor = msgType.GetConstructors()
            .FirstOrDefault(c => c.GetParameters().Length == 7)
            ?? throw new InvalidOperationException(
                $"NatsMsg<{payloadType.Name}> does not expose the expected 7 argument constructor. " +
                "The installed NATS.Net version is not compatible with this SDK.");

        var subject = Expression.Parameter(typeof(string), "subject");
        var replyTo = Expression.Parameter(typeof(string), "replyTo");
        var size = Expression.Parameter(typeof(int), "size");
        var headers = Expression.Parameter(typeof(NatsHeaders), "headers");
        var data = Expression.Parameter(typeof(object), "data");
        var connection = Expression.Parameter(typeof(INatsConnection), "connection");
        var flags = Expression.Parameter(typeof(NatsMsgFlags), "flags");

        var parameters = ctor.GetParameters();
        var arguments = new Expression[parameters.Length];
        for (int i = 0; i < parameters.Length; i++)
        {
            var name = parameters[i].Name ?? "";
            Expression source = name.ToLowerInvariant() switch
            {
                "subject" => subject,
                "replyto" => replyTo,
                "size" => size,
                "headers" => headers,
                "data" => data,
                "connection" => connection,
                "flags" => flags,
                _ => throw new InvalidOperationException(
                    $"Unexpected NatsMsg<{payloadType.Name}> constructor parameter '{parameters[i].Name}'. " +
                    "The installed NATS.Net version is not compatible with this SDK.")
            };

            arguments[i] = source.Type == parameters[i].ParameterType
                ? source
                : Expression.Convert(source, parameters[i].ParameterType);
        }

        return Expression.Lambda<Factory>(
            Expression.Convert(Expression.New(ctor, arguments), typeof(object)),
            subject, replyTo, size, headers, data, connection, flags).Compile();
    }

    /// <summary>
    /// Compiles a metadata reader for a concrete <c>NatsMsg&lt;T&gt;</c> type.
    /// </summary>
    private static Reader BuildReader(Type msgType)
    {
        var message = Expression.Parameter(typeof(object), "message");
        var typed = Expression.Convert(message, msgType);

        Expression Property(string name, Type expected)
        {
            var property = msgType.GetProperty(name, BindingFlags.Public | BindingFlags.Instance)
                ?? throw new InvalidOperationException(
                    $"{msgType.Name} does not expose a '{name}' property. " +
                    "The installed NATS.Net version is not compatible with this SDK.");

            Expression access = Expression.Property(typed, property);
            return access.Type == expected ? access : Expression.Convert(access, expected);
        }

        var ctor = typeof(NatsMsgMetadata).GetConstructors()[0];
        var body = Expression.New(
            ctor,
            Property(nameof(NatsMsg<object>.Subject), typeof(string)),
            Property(nameof(NatsMsg<object>.ReplyTo), typeof(string)),
            Property(nameof(NatsMsg<object>.Size), typeof(int)),
            Property(nameof(NatsMsg<object>.Headers), typeof(NatsHeaders)),
            Property(nameof(NatsMsg<object>.Connection), typeof(INatsConnection)),
            Property(nameof(NatsMsg<object>.Flags), typeof(NatsMsgFlags)));

        return Expression.Lambda<Reader>(body, message).Compile();
    }
}

/// <summary>
/// Transport metadata carried by a <see cref="NatsMsg{T}"/>, independent of the payload type.
/// </summary>
/// <param name="Subject">The subject the message was delivered on.</param>
/// <param name="ReplyTo">The reply subject for request/reply, if any.</param>
/// <param name="Size">The wire size of the message.</param>
/// <param name="Headers">The message headers, if any.</param>
/// <param name="Connection">The connection the message arrived on.</param>
/// <param name="Flags">The message flags.</param>
internal readonly record struct NatsMsgMetadata(
    string Subject,
    string? ReplyTo,
    int Size,
    NatsHeaders? Headers,
    INatsConnection? Connection,
    NatsMsgFlags Flags);
