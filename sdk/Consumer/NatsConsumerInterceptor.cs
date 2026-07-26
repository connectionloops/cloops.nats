using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using NATS.Client.Core;

namespace CLOOPS.NATS;

/// <summary>
/// Runs before a NATS consumer handler receives a message.
/// </summary>
public interface INatsConsumerInterceptor
{
    /// <summary>
    /// Inspects, replaces, or rejects a consumer message before the handler runs.
    /// </summary>
    /// <param name="context">The current consumer message and metadata.</param>
    /// <param name="ct">Cancellation token for the interceptor operation.</param>
    /// <returns>A result indicating whether processing should continue to the next interceptor or handler.</returns>
    ValueTask<NatsConsumerInterceptorResult> InterceptAsync(NatsConsumerInterceptorContext context, CancellationToken ct = default);
}

/// <summary>
/// Result returned by a NATS consumer interceptor.
/// </summary>
public sealed class NatsConsumerInterceptorResult
{
    private NatsConsumerInterceptorResult(bool shouldContinue, bool shouldRetryDelivery, string? reason, object? reply)
    {
        ShouldContinue = shouldContinue;
        ShouldRetryDelivery = shouldRetryDelivery;
        Reason = reason;
        Reply = reply;
    }

    /// <summary>
    /// Indicates whether processing should continue to the next interceptor or handler.
    /// </summary>
    public bool ShouldContinue { get; }

    /// <summary>
    /// Indicates whether a rejected JetStream message should be redelivered.
    /// </summary>
    public bool ShouldRetryDelivery { get; }

    /// <summary>
    /// Optional reason recorded in logs when an interceptor rejects the message.
    /// </summary>
    public string? Reason { get; }

    /// <summary>
    /// Optional reply payload for Core NATS request/reply short-circuits.
    /// When set and the inbound message has a <c>ReplyTo</c>, the platform sends this reply
    /// before stopping the pipeline. Ignored for JetStream rejects and Core pub/sub messages.
    /// </summary>
    public object? Reply { get; }

    /// <summary>
    /// Allows processing to continue.
    /// </summary>
    /// <returns>A result that continues the consumer pipeline.</returns>
    public static NatsConsumerInterceptorResult Continue()
        => new(true, false, null, null);

    /// <summary>
    /// Stops the consumer pipeline before the handler runs.
    /// </summary>
    /// <param name="reason">Optional reason to include in logs.</param>
    /// <param name="shouldRetryDelivery">Whether JetStream should redeliver the rejected message.</param>
    /// <param name="reply">
    /// Optional reply payload for Core NATS request/reply. When provided and the message has a
    /// <c>ReplyTo</c>, the platform replies with this value and does not invoke the handler.
    /// </param>
    /// <returns>A result that rejects the message.</returns>
    public static NatsConsumerInterceptorResult Reject(
        string? reason = null,
        bool shouldRetryDelivery = false,
        object? reply = null)
        => new(false, shouldRetryDelivery, reason, reply);
}

/// <summary>
/// Context passed to every NATS consumer interceptor.
/// </summary>
public sealed class NatsConsumerInterceptorContext
{
    /// <summary>
    /// Creates a new interceptor context for a consumer message.
    /// </summary>
    /// <param name="matchedSubject">The configured consumer subject that matched the message.</param>
    /// <param name="payloadType">The handler payload type.</param>
    /// <param name="handlerType">The consumer handler class type.</param>
    /// <param name="handlerMethod">The consumer handler method.</param>
    /// <param name="message">The typed NATS message that will be passed to the handler. Must be a <see cref="NatsMsg{T}"/> of <paramref name="payloadType"/>.</param>
    /// <param name="rawData">The original serialized message bytes.</param>
    /// <param name="jetStream">JetStream delivery information, or <c>null</c> for Core NATS messages.</param>
    /// <exception cref="ArgumentNullException">Thrown when a required argument is null.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="message"/> is not a <see cref="NatsMsg{T}"/> of <paramref name="payloadType"/>.</exception>
    /// <remarks>
    /// The platform builds this context for you. It is public so that you can construct one
    /// directly in unit tests for your own <see cref="INatsConsumerInterceptor"/> implementations.
    /// </remarks>
    public NatsConsumerInterceptorContext(
        string matchedSubject,
        Type payloadType,
        Type handlerType,
        MethodInfo handlerMethod,
        object message,
        byte[]? rawData,
        NatsConsumerJetStreamMetadata? jetStream = null)
    {
        ArgumentNullException.ThrowIfNull(matchedSubject);
        ArgumentNullException.ThrowIfNull(payloadType);
        ArgumentNullException.ThrowIfNull(handlerType);
        ArgumentNullException.ThrowIfNull(handlerMethod);
        ArgumentNullException.ThrowIfNull(message);
        NatsMsgAccessor.EnsureTypedMessage(message, payloadType, nameof(message));

        MatchedSubject = matchedSubject;
        PayloadType = payloadType;
        HandlerType = handlerType;
        HandlerMethod = handlerMethod;
        currentMessage = message;
        RawData = rawData;
        JetStream = jetStream;
    }

    private object currentMessage;

    private NatsMsgMetadata? metadata;

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
    /// The typed NATS message that will be passed to the handler.
    /// </summary>
    public object Message => currentMessage;

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
    /// where <see cref="NatsConsumerInterceptorResult.ShouldRetryDelivery"/> is ignored and
    /// <see cref="NatsConsumerInterceptorResult.Reply"/> is honoured. When <c>true</c>, the reverse applies.
    /// </summary>
    public bool IsJetStream => JetStream.HasValue;

    /// <summary>
    /// The message headers.
    /// </summary>
    public NatsHeaders? Headers => Metadata.Headers;

    /// <summary>
    /// The reply subject, if this message uses request/reply.
    /// </summary>
    public string? ReplyTo => Metadata.ReplyTo;

    /// <summary>
    /// Transport metadata for the current message, re-read only after an interceptor replaces it.
    /// </summary>
    private NatsMsgMetadata Metadata => metadata ??= NatsMsgAccessor.Read(currentMessage);

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
            $"Interceptor requested NatsMsg<{typeof(TPayload).Name}> but the handler expects NatsMsg<{PayloadType.Name}>.");
    }

    /// <summary>
    /// Replaces the full message that will be passed to the handler.
    /// </summary>
    /// <typeparam name="TPayload">Message payload type.</typeparam>
    /// <param name="message">Replacement message.</param>
    /// <exception cref="InvalidOperationException">Thrown when the replacement payload type does not match the handler payload type.</exception>
    public void ReplaceMessage<TPayload>(NatsMsg<TPayload> message)
    {
        EnsurePayloadType<TPayload>();
        SetMessage(message);
    }

    /// <summary>
    /// Replaces only the payload while preserving the subject, reply subject, headers, connection, and flags.
    /// </summary>
    /// <typeparam name="TPayload">Payload type.</typeparam>
    /// <param name="payload">Replacement payload.</param>
    /// <exception cref="InvalidOperationException">Thrown when the replacement payload type does not match the handler payload type.</exception>
    public void ReplaceData<TPayload>(TPayload? payload)
    {
        EnsurePayloadType<TPayload>();

        // Rebuild the immutable NatsMsg<T> wrapper so downstream handlers see the modified payload.
        SetMessage(BaseNatsUtil.CreateTypedMsgWrapper(currentMessage, PayloadType, payload));
    }

    /// <summary>
    /// Swaps the current message and drops the cached metadata read from the previous one.
    /// </summary>
    private void SetMessage(object replacement)
    {
        currentMessage = replacement;
        metadata = null;
    }

    private void EnsurePayloadType<TPayload>()
    {
        if (PayloadType != typeof(TPayload))
        {
            throw new InvalidOperationException(
                $"Interceptor replacement type {typeof(TPayload).Name} does not match handler payload type {PayloadType.Name}.");
        }
    }
}

/// <summary>
/// Registers NATS consumer interceptors with dependency injection.
/// </summary>
public static class NatsConsumerInterceptorServiceCollectionExtensions
{
    /// <summary>
    /// Adds a NATS consumer interceptor. Interceptors run in registration order.
    /// </summary>
    /// <typeparam name="TInterceptor">Interceptor implementation type.</typeparam>
    /// <param name="services">Service collection.</param>
    /// <returns>The same service collection for chaining.</returns>
    public static IServiceCollection AddNatsConsumerInterceptor<TInterceptor>(this IServiceCollection services)
        where TInterceptor : class, INatsConsumerInterceptor
    {
        services.AddSingleton<INatsConsumerInterceptor, TInterceptor>();
        return services;
    }
}
