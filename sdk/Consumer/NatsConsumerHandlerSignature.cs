using System.Reflection;
using CLOOPS.NATS.Meta;
using NATS.Client.Core;

namespace CLOOPS.NATS;

/// <summary>
/// Shared validation for the NATS consumer handler contract:
/// <c>Task&lt;NatsAck&gt; Handler(NatsMsg&lt;T&gt; msg, CancellationToken ct = default)</c>.
/// </summary>
/// <remarks>
/// Used both by the attribute driven discovery path (validated lazily during
/// <c>NatsSubscriptionProcessor.Setup</c>) and by <see cref="NatsDynamicConsumer"/>
/// (validated eagerly, at construction time, so a bad runtime registration fails
/// where the caller can see it).
/// </remarks>
internal static class NatsConsumerHandlerSignature
{
    /// <summary>
    /// Validates the handler signature and returns <c>T</c> from its <c>NatsMsg&lt;T&gt;</c> parameter.
    /// </summary>
    /// <param name="handler">The handler method to validate.</param>
    /// <returns>The payload type the handler consumes.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the handler does not match the contract.</exception>
    internal static Type GetPayloadType(MethodInfo handler)
    {
        var parameters = handler.GetParameters();
        if (parameters.Length != 2)
        {
            throw new InvalidOperationException($"Invalid Handler: {handler.Name} must have exactly 2 parameters: (NatsMsg<T> payload, CancellationToken).");
        }

        var messageType = parameters[0].ParameterType;
        if (!messageType.IsGenericType || messageType.GetGenericTypeDefinition() != typeof(NatsMsg<>))
        {
            throw new InvalidOperationException($"Consumer method {handler.Name} parameter[0] must be of type NatsMsg<T>.");
        }

        var messageGenericArguments = messageType.GetGenericArguments();
        if (messageGenericArguments.Length != 1)
        {
            throw new InvalidOperationException($"Invalid Handler: {handler.Name} must define exactly one generic type argument for its payload.");
        }

        // validate return type
        var rt = handler.ReturnType;
        bool isReturnTypeValid =
            rt.IsGenericType &&
            rt.GetGenericTypeDefinition() == typeof(Task<>) &&
            rt.GetGenericArguments()[0] == typeof(NatsAck);

        if (!isReturnTypeValid)
            throw new InvalidOperationException(
                $"Handler {handler.DeclaringType?.Name}.{handler.Name} must return Task<NatsAck>.");

        return messageGenericArguments[0];
    }
}
