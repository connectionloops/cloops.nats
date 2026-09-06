using System.Reflection;
using CLOOPS.NATS.Attributes;
using Microsoft.Extensions.DependencyInjection;

namespace CLOOPS.NATS;

/// <summary>
/// Describes a NATS consumer that is discovered at runtime instead of being declared with
/// <see cref="NatsConsumerAttribute"/>.
/// </summary>
/// <remarks>
/// <para>
/// Use this when the set of consumers is not known at compile time - for example when a service
/// attaches to a set of JetStream durable consumers ("lanes") that are created and re-sharded in
/// the control plane. The same handler method can be bound to many subjects / consumer ids by
/// creating one <see cref="NatsDynamicConsumer"/> per binding, which is not expressible with
/// <see cref="NatsConsumerAttribute"/> (it is not <c>AllowMultiple</c>).
/// </para>
/// <para>
/// The handler contract is identical to the attribute based one and is validated eagerly here:
/// <c>Task&lt;NatsAck&gt; Handler(NatsMsg&lt;T&gt; msg, CancellationToken ct = default)</c>.
/// </para>
/// <para>
/// <see cref="HandlerType"/> must be resolvable from the application's service provider; it is
/// resolved with <c>GetRequiredService</c> when the consumer is registered, exactly like an
/// attribute decorated handler class.
/// </para>
/// <example>
/// <code>
/// var consumer = new NatsDynamicConsumer(
///     subject: "cbb.lane.7.>",
///     handlerType: typeof(LaneConsumer),
///     handlerMethod: typeof(LaneConsumer).GetMethod(nameof(LaneConsumer.Handle))!,
///     consumerId: "cbb-lane-7");
/// </code>
/// </example>
/// </remarks>
public sealed class NatsDynamicConsumer
{
    /// <summary>
    /// The subject (or subject filter) this consumer listens to. Wildcards (<c>*</c> and <c>&gt;</c>) are supported.
    /// </summary>
    public string Subject { get; }

    /// <summary>
    /// Durable JetStream consumer id, or <c>null</c> for a core NATS subscription.
    /// Mirrors <see cref="NatsConsumerAttribute"/>: supplying a consumer id makes the consumer durable.
    /// The consumer must already exist - the SDK attaches to it, it never creates it.
    /// </summary>
    public string? ConsumerId { get; }

    /// <summary>
    /// NATS queue group name. Only meaningful for core subscriptions.
    /// Supports the same placeholders as <see cref="NatsConsumerAttribute.QueueGroupName"/>.
    /// </summary>
    public string QueueGroupName { get; }

    /// <summary>
    /// The class declaring the handler. Resolved from DI when the consumer is registered.
    /// </summary>
    public Type HandlerType { get; }

    /// <summary>
    /// The handler method to invoke for messages on <see cref="Subject"/>.
    /// </summary>
    public MethodInfo HandlerMethod { get; }

    /// <summary>
    /// The payload type <c>T</c> extracted from the handler's <c>NatsMsg&lt;T&gt;</c> parameter.
    /// </summary>
    public Type PayloadType { get; }

    /// <summary>
    /// True when <see cref="ConsumerId"/> was supplied, i.e. this is a durable JetStream consumer.
    /// </summary>
    public bool IsDurable => Attribute.IsDurable;

    /// <summary>
    /// The equivalent consumer attribute. Registration goes through exactly the same code path
    /// as attribute discovered consumers.
    /// </summary>
    internal NatsConsumerAttribute Attribute { get; }

    /// <summary>
    /// Creates a runtime consumer registration.
    /// </summary>
    /// <param name="subject">Subject or subject filter to bind. Wildcards are supported.</param>
    /// <param name="handlerType">Class declaring <paramref name="handlerMethod"/>. Must be resolvable from DI.</param>
    /// <param name="handlerMethod">Handler method. Must be <c>Task&lt;NatsAck&gt; M(NatsMsg&lt;T&gt;, CancellationToken)</c>.</param>
    /// <param name="consumerId">Durable JetStream consumer id. Omit for a core NATS subscription.</param>
    /// <param name="queueGroupName">Queue group name, core subscriptions only.</param>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="ArgumentException">The subject / consumer id is blank, or the method does not belong to <paramref name="handlerType"/>.</exception>
    /// <exception cref="InvalidOperationException">The handler signature does not match the consumer contract.</exception>
    public NatsDynamicConsumer(
        string subject,
        Type handlerType,
        MethodInfo handlerMethod,
        string? consumerId = null,
        string queueGroupName = "")
    {
        ArgumentNullException.ThrowIfNull(handlerType);
        ArgumentNullException.ThrowIfNull(handlerMethod);

        if (string.IsNullOrWhiteSpace(subject))
        {
            throw new ArgumentException("Dynamic consumer subject must not be null or blank.", nameof(subject));
        }

        if (consumerId is not null && string.IsNullOrWhiteSpace(consumerId))
        {
            throw new ArgumentException("Dynamic consumer id must not be blank. Pass null for a core NATS subscription.", nameof(consumerId));
        }

        var declaringType = handlerMethod.DeclaringType;
        if (declaringType is null || !declaringType.IsAssignableFrom(handlerType))
        {
            throw new ArgumentException(
                $"Handler method {handlerMethod.Name} is declared on {declaringType?.FullName ?? "<unknown>"} which is not assignable from handler type {handlerType.FullName}.",
                nameof(handlerMethod));
        }

        if (handlerMethod.IsGenericMethodDefinition)
        {
            throw new ArgumentException(
                $"Handler method {handlerMethod.Name} is an open generic method definition and cannot be used as a NATS consumer.",
                nameof(handlerMethod));
        }

        // Fail loudly, at registration time, on a bad handler signature.
        PayloadType = NatsConsumerHandlerSignature.GetPayloadType(handlerMethod);

        Subject = subject;
        ConsumerId = consumerId;
        QueueGroupName = queueGroupName ?? "";
        HandlerType = handlerType;
        HandlerMethod = handlerMethod;
        Attribute = new NatsConsumerAttribute(subject, consumerId, QueueGroupName);
    }
}

/// <summary>
/// Supplies NATS consumers that are only known at runtime.
/// </summary>
/// <remarks>
/// <para>
/// Register an implementation with
/// <see cref="NatsDynamicConsumerServiceCollectionExtensions.AddNatsDynamicConsumerSource{TSource}"/>
/// before the host starts. All registered sources are resolved and awaited once, at the start of
/// <see cref="ICloopsNatsClient.MapConsumers"/>, and their consumers are registered alongside the
/// ones discovered from <see cref="NatsConsumerAttribute"/>.
/// </para>
/// <para>
/// The call is asynchronous so an implementation can query NATS (for example, list the durable
/// consumers that currently exist on a stream) before deciding what to bind. It runs while the
/// host is starting, so keep it bounded - a hanging source blocks consumer startup.
/// </para>
/// <para>
/// Sources are resolved as singletons, in registration order, at <c>MapConsumers</c> time. Like
/// interceptors and exception handlers, they cannot depend on scoped services; inject
/// <see cref="IServiceProvider"/> and create a scope if you need one.
/// </para>
/// </remarks>
public interface INatsDynamicConsumerSource
{
    /// <summary>
    /// Returns the consumers to register. Return an empty collection to register nothing.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    ValueTask<IReadOnlyCollection<NatsDynamicConsumer>> GetConsumersAsync(CancellationToken ct = default);
}

/// <summary>
/// Registers runtime NATS consumer sources with dependency injection.
/// </summary>
public static class NatsDynamicConsumerServiceCollectionExtensions
{
    /// <summary>
    /// Adds a runtime NATS consumer source. Sources are queried in registration order when
    /// consumers are mapped.
    /// </summary>
    /// <typeparam name="TSource">Source implementation type.</typeparam>
    /// <param name="services">Service collection.</param>
    /// <returns>The same service collection for chaining.</returns>
    public static IServiceCollection AddNatsDynamicConsumerSource<TSource>(this IServiceCollection services)
        where TSource : class, INatsDynamicConsumerSource
    {
        services.AddSingleton<INatsDynamicConsumerSource, TSource>();
        return services;
    }
}
