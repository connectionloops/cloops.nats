using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.Extensions.Logging;

namespace CLOOPS.NATS;

/// <summary>
/// Thrown when a payload's own <c>Validate()</c> method rejects an incoming message.
/// </summary>
/// <remarks>
/// This wrapper never reaches an <see cref="INatsConsumerExceptionHandler"/>: the platform hands
/// handlers the exception the schema itself threw (see <see cref="Exception.InnerException"/>), so a
/// handler that already classifies its schema's validation exception keeps working unchanged. The
/// wrapper exists only so the platform can tell a pre-dispatch validation rejection apart from a
/// fault thrown by the consumer handler.
/// </remarks>
internal sealed class NatsMessageValidationException : Exception
{
    /// <summary>
    /// Creates a validation failure for a message the platform refused to dispatch.
    /// </summary>
    /// <param name="subject">The configured consumer subject that matched the message.</param>
    /// <param name="payloadType">The handler payload type whose <c>Validate()</c> rejected the message.</param>
    /// <param name="innerException">The exception the payload's <c>Validate()</c> threw, carrying the validation results.</param>
    internal NatsMessageValidationException(string subject, Type payloadType, Exception innerException)
        : base($"Message validation failed for subject {subject} with payload type {payloadType.Name}.", innerException)
    {
        Subject = subject;
        PayloadType = payloadType;
    }

    /// <summary>
    /// The configured consumer subject that matched the message.
    /// </summary>
    internal string Subject { get; }

    /// <summary>
    /// The handler payload type whose <c>Validate()</c> rejected the message.
    /// </summary>
    internal Type PayloadType { get; }
}

/// <summary>
/// Runs the optional <c>Validate()</c> method a payload type may declare, before the message is
/// dispatched to the consumer handler.
/// </summary>
/// <remarks>
/// The contract is duck typed on purpose: any payload exposing a public parameterless
/// <c>Validate()</c> is validated, and whatever that method throws is the description of the
/// failure. Reflection lookups are cached per type in concurrent caches because validation runs on
/// the worker pool, not on the single subscription loop.
/// </remarks>
internal static class NatsMessageValidator
{
    // Validate() method per payload type, to avoid the reflection lookup per message.
    private static readonly ConcurrentDictionary<Type, MethodInfo?> ValidateMethodCache = new();

    // Data property getter per message wrapper type, to avoid the reflection lookup per message.
    private static readonly ConcurrentDictionary<Type, PropertyInfo?> DataPropertyCache = new();

    /// <summary>
    /// Validates a message payload if its type declares a <c>Validate()</c> method.
    /// Does nothing if no validation method exists.
    /// </summary>
    /// <param name="msgObject">The typed message wrapper (<c>NatsMsg&lt;T&gt;</c>) handed to the handler.</param>
    /// <param name="payloadType">The handler payload type.</param>
    /// <param name="subject">The configured consumer subject that matched the message.</param>
    /// <param name="logger">Logger used to report a wrapper without a <c>Data</c> property.</param>
    /// <exception cref="NatsMessageValidationException">Thrown when validation fails.</exception>
    internal static void ValidateOrThrow(object msgObject, Type payloadType, string subject, ILogger logger)
    {
        var validateMethod = GetValidateMethod(payloadType);
        if (validateMethod == null)
        {
            // No Validate() method - skip validation
            return;
        }

        try
        {
            // Extract the payload from NatsMsg<T> - it's the Data property
            var dataProperty = GetDataProperty(msgObject.GetType());
            if (dataProperty == null)
            {
                logger.LogWarning("Message wrapper for subject {Subject} does not have a Data property", subject);
                return; // Can't validate, but don't block processing
            }

            var payload = dataProperty.GetValue(msgObject);
            if (payload == null)
            {
                // Null payload - skip validation
                return;
            }

            // Call Validate() on the payload
            validateMethod.Invoke(payload, null);
        }
        catch (Exception ex)
        {
            // Validation failed - wrap so the caller can tell this apart from a handler fault.
            throw new NatsMessageValidationException(subject, payloadType, ex);
        }
    }

    /// <summary>
    /// Gets or caches the <c>Validate()</c> method for a given payload type.
    /// Returns null if the type doesn't have a <c>Validate()</c> method.
    /// </summary>
    private static MethodInfo? GetValidateMethod(Type payloadType)
        => ValidateMethodCache.GetOrAdd(
            payloadType,
            // A public instance method named "Validate" taking no parameters.
            static type => type.GetMethod("Validate", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null));

    /// <summary>
    /// Gets or caches the <c>Data</c> property for a given message wrapper type.
    /// Returns null if the type doesn't have a <c>Data</c> property.
    /// </summary>
    private static PropertyInfo? GetDataProperty(Type msgWrapperType)
        => DataPropertyCache.GetOrAdd(msgWrapperType, static type => type.GetProperty("Data"));
}
