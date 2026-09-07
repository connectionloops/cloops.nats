using System.Reflection;
using CLOOPS.NATS.Meta;
using Microsoft.Extensions.DependencyInjection;
using NATS.Client.Core;

namespace CLOOPS.NATS;

/// <summary>
/// Maps exceptions thrown by a NATS consumer handler to a <see cref="NatsAck"/>.
/// </summary>
/// <remarks>
/// Exception handlers run after a handler throws (JetStream or Core). Unlike
/// <see cref="INatsConsumerInterceptor"/>, they do not run before the handler.
/// Handlers are resolved from DI and evaluated in registration order; the first
/// non-null <see cref="NatsAck"/> is applied. If none handle the exception, the
/// platform preserves the previous behavior (log and rethrow / JetStream redelivery).
/// </remarks>
public interface INatsConsumerExceptionHandler
{
    /// <summary>
    /// Converts a handler exception into a <see cref="NatsAck"/>, or declines to handle it.
    /// </summary>
    /// <param name="context">The thrown exception and consumer invocation metadata.</param>
    /// <param name="ct">Cancellation token for the exception-handler operation.</param>
    /// <returns>
    /// A <see cref="NatsAck"/> to apply for the failed invocation, or <c>null</c> to let the
    /// next registered exception handler (or the platform default) take over.
    /// </returns>
    ValueTask<NatsAck?> HandleExceptionAsync(NatsConsumerExceptionContext context, CancellationToken ct = default);
}

/// <summary>
/// Context passed to every NATS consumer exception handler.
/// </summary>
public sealed class NatsConsumerExceptionContext
{
    /// <summary>
    /// Creates a new exception context for a failed consumer handler invocation.
    /// </summary>
    /// <param name="matchedSubject">The configured consumer subject that matched the message.</param>
    /// <param name="payloadType">The handler payload type.</param>
    /// <param name="handlerType">The consumer handler class type.</param>
    /// <param name="handlerMethod">The consumer handler method.</param>
    /// <param name="message">The typed NATS message that was passed to the handler. Must be a <see cref="NatsMsg{T}"/> of <paramref name="payloadType"/>.</param>
    /// <param name="exception">The exception thrown by the handler (unwrapped from reflection wrappers).</param>
    /// <param name="rawData">The original serialized message bytes.</param>
    /// <param name="jetStream">JetStream delivery information, or <c>null</c> for Core NATS messages.</param>
    /// <exception cref="ArgumentNullException">Thrown when a required argument is null.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="message"/> is not a <see cref="NatsMsg{T}"/> of <paramref name="payloadType"/>.</exception>
    /// <remarks>
    /// The platform builds this context for you. It is public so that you can construct one
    /// directly in unit tests for your own <see cref="INatsConsumerExceptionHandler"/> implementations.
    /// </remarks>
    public NatsConsumerExceptionContext(
        string matchedSubject,
        Type payloadType,
        Type handlerType,
        MethodInfo handlerMethod,
        object message,
        Exception exception,
        byte[]? rawData,
        NatsConsumerJetStreamMetadata? jetStream = null)
        : this(matchedSubject, payloadType, handlerType, handlerMethod, message, exception, rawData, jetStream, false)
    {
    }

    /// <summary>
    /// Creates a new exception context, stating whether the failure came from payload validation
    /// rather than from the handler itself.
    /// </summary>
    /// <param name="matchedSubject">The configured consumer subject that matched the message.</param>
    /// <param name="payloadType">The handler payload type.</param>
    /// <param name="handlerType">The consumer handler class type.</param>
    /// <param name="handlerMethod">The consumer handler method.</param>
    /// <param name="message">The typed NATS message that was passed to the handler. Must be a <see cref="NatsMsg{T}"/> of <paramref name="payloadType"/>.</param>
    /// <param name="exception">The exception thrown by the handler, or by the payload's own <c>Validate()</c> (unwrapped from reflection wrappers).</param>
    /// <param name="rawData">The original serialized message bytes.</param>
    /// <param name="jetStream">JetStream delivery information, or <c>null</c> for Core NATS messages.</param>
    /// <param name="isValidationFailure"><c>true</c> when the payload failed its own <c>Validate()</c> and the handler was never invoked.</param>
    /// <exception cref="ArgumentNullException">Thrown when a required argument is null.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="message"/> is not a <see cref="NatsMsg{T}"/> of <paramref name="payloadType"/>.</exception>
    /// <remarks>
    /// This is a separate overload rather than one more optional parameter on the constructor above:
    /// C# binds optional arguments at the call site, so adding one to a shipped signature is a
    /// binary break for every already-compiled caller.
    /// </remarks>
    public NatsConsumerExceptionContext(
        string matchedSubject,
        Type payloadType,
        Type handlerType,
        MethodInfo handlerMethod,
        object message,
        Exception exception,
        byte[]? rawData,
        NatsConsumerJetStreamMetadata? jetStream,
        bool isValidationFailure)
    {
        ArgumentNullException.ThrowIfNull(matchedSubject);
        ArgumentNullException.ThrowIfNull(payloadType);
        ArgumentNullException.ThrowIfNull(handlerType);
        ArgumentNullException.ThrowIfNull(handlerMethod);
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(exception);
        NatsMsgAccessor.EnsureTypedMessage(message, payloadType, nameof(message));

        MatchedSubject = matchedSubject;
        PayloadType = payloadType;
        HandlerType = handlerType;
        HandlerMethod = handlerMethod;
        Message = message;
        Exception = exception;
        RawData = rawData;
        JetStream = jetStream;
        IsValidationFailure = isValidationFailure;
    }

    /// <summary>
    /// The configured consumer subject that matched the incoming message.
    /// </summary>
    public string MatchedSubject { get; }

    /// <summary>
    /// The actual incoming message subject.
    /// </summary>
    public string Subject => Metadata.Subject ?? MatchedSubject;

    /// <summary>
    /// The handler payload type.
    /// </summary>
    public Type PayloadType { get; }

    /// <summary>
    /// The consumer handler class type.
    /// </summary>
    public Type HandlerType { get; }

    /// <summary>
    /// The consumer handler method.
    /// </summary>
    public MethodInfo HandlerMethod { get; }

    /// <summary>
    /// The typed NATS message that was passed to the handler.
    /// </summary>
    public object Message { get; }

    /// <summary>
    /// The exception thrown by the handler (unwrapped from <see cref="TargetInvocationException"/> when applicable).
    /// </summary>
    public Exception Exception { get; }

    /// <summary>
    /// The original serialized message bytes received from NATS.
    /// </summary>
    public byte[]? RawData { get; }

    /// <summary>
    /// Read-only JetStream delivery information, or <c>null</c> when the message arrived over Core NATS.
    /// </summary>
    public NatsConsumerJetStreamMetadata? JetStream { get; }

    /// <summary>
    /// Whether this message arrived over JetStream. When <c>false</c>, the transport is Core NATS,
    /// where <see cref="NatsAck.ShouldRetryDelivery"/> is ignored and <see cref="NatsAck.Reply"/> is
    /// honoured. When <c>true</c>, the reverse applies.
    /// </summary>
    public bool IsJetStream => JetStream.HasValue;

    /// <summary>
    /// Whether <see cref="Exception"/> came from the payload's own <c>Validate()</c> instead of from
    /// the consumer handler. When <c>true</c> the handler was never invoked and nothing was done with
    /// the message, so a reply produced here is the only answer the caller will get.
    /// </summary>
    /// <remarks>
    /// The exception is the one the payload's <c>Validate()</c> threw, not a platform wrapper, so
    /// handlers that already classify their schema's validation exception keep working. On JetStream
    /// a validation failure is never redelivered: an ack asking for redelivery is downgraded to a
    /// terminate, because a payload that failed validation cannot become valid on a retry.
    /// </remarks>
    public bool IsValidationFailure { get; }

    /// <summary>
    /// The message headers.
    /// </summary>
    public NatsHeaders? Headers => Metadata.Headers;

    /// <summary>
    /// The reply subject, if this message uses request/reply.
    /// </summary>
    public string? ReplyTo => Metadata.ReplyTo;

    /// <summary>
    /// Transport metadata read once from <see cref="Message"/>, which never changes for a given context.
    /// </summary>
    private NatsMsgMetadata Metadata => metadata ??= NatsMsgAccessor.Read(Message);

    private NatsMsgMetadata? metadata;

    /// <summary>
    /// Gets the current message as a typed <see cref="NatsMsg{T}"/>.
    /// </summary>
    /// <typeparam name="TPayload">Expected payload type.</typeparam>
    /// <returns>The typed NATS message.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the message payload type does not match the handler payload type.</exception>
    public NatsMsg<TPayload> GetMessage<TPayload>()
    {
        if (Message is NatsMsg<TPayload> typedMessage)
            return typedMessage;

        throw new InvalidOperationException(
            $"Exception handler requested NatsMsg<{typeof(TPayload).Name}> but the handler expects NatsMsg<{PayloadType.Name}>.");
    }
}

/// <summary>
/// Registers NATS consumer exception handlers with dependency injection.
/// </summary>
public static class NatsConsumerExceptionHandlerServiceCollectionExtensions
{
    /// <summary>
    /// Adds a NATS consumer exception handler. Handlers run in registration order;
    /// the first non-null <see cref="NatsAck"/> wins.
    /// </summary>
    /// <typeparam name="TExceptionHandler">Exception handler implementation type.</typeparam>
    /// <param name="services">Service collection.</param>
    /// <returns>The same service collection for chaining.</returns>
    public static IServiceCollection AddNatsConsumerExceptionHandler<TExceptionHandler>(this IServiceCollection services)
        where TExceptionHandler : class, INatsConsumerExceptionHandler
    {
        services.AddSingleton<INatsConsumerExceptionHandler, TExceptionHandler>();
        return services;
    }
}
