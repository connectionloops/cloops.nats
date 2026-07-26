using System.Linq.Expressions;
using System.Reflection;
using CLOOPS.NATS.Meta;

namespace CLOOPS.NATS;

/// <summary>
/// Everything the message pipeline needs to invoke one consumer handler, resolved once at startup.
/// </summary>
/// <remarks>
/// Holding this per subject keeps the per-message work item closure small (it captures a single
/// plan reference instead of the handler method, instance, payload type, and handler type) and lets
/// the handler be called through a compiled delegate rather than
/// <see cref="MethodBase.Invoke(object, object[])"/>, which allocates an argument array and boxes
/// the <see cref="CancellationToken"/> on every message.
/// </remarks>
internal sealed class NatsConsumerInvocationPlan
{
    /// <summary>
    /// Builds the plan and compiles the handler invoker.
    /// </summary>
    /// <param name="matchedSubject">The configured consumer subject this plan serves.</param>
    /// <param name="payloadType">The handler payload type.</param>
    /// <param name="handlerType">The consumer handler class type.</param>
    /// <param name="handlerMethod">The consumer handler method.</param>
    /// <param name="handlerInstance">The DI-resolved handler class instance.</param>
    internal NatsConsumerInvocationPlan(
        string matchedSubject,
        Type payloadType,
        Type handlerType,
        MethodInfo handlerMethod,
        object handlerInstance)
    {
        MatchedSubject = matchedSubject;
        PayloadType = payloadType;
        HandlerType = handlerType;
        HandlerMethod = handlerMethod;
        HandlerName = handlerMethod.Name;
        Invoke = CompileInvoker(handlerMethod, handlerInstance);
    }

    /// <summary>
    /// The configured consumer subject this plan serves.
    /// </summary>
    internal string MatchedSubject { get; }

    /// <summary>
    /// The handler payload type.
    /// </summary>
    internal Type PayloadType { get; }

    /// <summary>
    /// The consumer handler class type.
    /// </summary>
    internal Type HandlerType { get; }

    /// <summary>
    /// The consumer handler method.
    /// </summary>
    internal MethodInfo HandlerMethod { get; }

    /// <summary>
    /// The handler method name, cached for metrics tagging.
    /// </summary>
    internal string HandlerName { get; }

    /// <summary>
    /// Calls the handler with a boxed <c>NatsMsg&lt;T&gt;</c>.
    /// </summary>
    /// <remarks>
    /// Exceptions thrown by the handler body propagate directly instead of being wrapped in a
    /// <see cref="TargetInvocationException"/>, so the original stack trace is preserved.
    /// </remarks>
    internal Func<object, CancellationToken, Task<NatsAck>> Invoke { get; }

    /// <summary>
    /// Compiles a delegate that casts the boxed message to the handler's declared parameter type
    /// and calls the handler on a fixed instance.
    /// </summary>
    private static Func<object, CancellationToken, Task<NatsAck>> CompileInvoker(
        MethodInfo handlerMethod,
        object handlerInstance)
    {
        var messageParameterType = handlerMethod.GetParameters()[0].ParameterType;

        var message = Expression.Parameter(typeof(object), "message");
        var ct = Expression.Parameter(typeof(CancellationToken), "ct");

        var call = Expression.Call(
            Expression.Constant(handlerInstance, handlerMethod.DeclaringType ?? handlerInstance.GetType()),
            handlerMethod,
            Expression.Convert(message, messageParameterType),
            ct);

        return Expression.Lambda<Func<object, CancellationToken, Task<NatsAck>>>(call, message, ct).Compile();
    }
}
