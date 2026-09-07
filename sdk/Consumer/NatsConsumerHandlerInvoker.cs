using System.Reflection;
using System.Runtime.ExceptionServices;
using CLOOPS.NATS.Meta;
using Microsoft.Extensions.Logging;

namespace CLOOPS.NATS;

/// <summary>
/// Invokes a discovered consumer handler and routes thrown exceptions through registered
/// <see cref="INatsConsumerExceptionHandler"/> instances.
/// </summary>
internal static class NatsConsumerHandlerInvoker
{
    /// <summary>
    /// Validates the payload and, if it passes, invokes the handler described by <paramref name="plan"/>.
    /// </summary>
    /// <param name="plan">The compiled invocation plan for the matched subject.</param>
    /// <param name="msgObject">The typed message wrapper passed to the handler.</param>
    /// <param name="rawData">Original serialized message bytes.</param>
    /// <param name="jetStream">JetStream delivery information, or <c>null</c> for Core NATS messages.</param>
    /// <param name="exceptionHandlers">Registered exception handlers in DI order.</param>
    /// <param name="logger">Logger used to record mapped exceptions and misbehaving exception handlers.</param>
    /// <param name="ct">Cancellation token passed to the handler and exception handlers.</param>
    /// <returns>
    /// The ack to apply, or <c>null</c> when the payload failed validation and no exception handler
    /// claimed it. A <c>null</c> return means the platform never dispatched the message and nothing
    /// mapped the failure, so the caller applies its transport's discard policy - which is what the
    /// platform did for every validation failure before exception handlers were reachable from here.
    /// </returns>
    /// <remarks>
    /// A validation failure is reported to exception handlers exactly like a handler fault, with one
    /// difference: <see cref="NatsConsumerExceptionContext.IsValidationFailure"/> is <c>true</c> and
    /// the handler was never invoked. Handlers see the exception the payload's own <c>Validate()</c>
    /// threw, not a platform wrapper, so a handler that already classifies its schema's validation
    /// exception needs no change.
    /// </remarks>
    internal static async Task<NatsAck?> InvokeWithValidationAsync(
        NatsConsumerInvocationPlan plan,
        object msgObject,
        byte[]? rawData,
        NatsConsumerJetStreamMetadata? jetStream,
        IReadOnlyList<INatsConsumerExceptionHandler> exceptionHandlers,
        ILogger logger,
        CancellationToken ct)
    {
        try
        {
            NatsMessageValidator.ValidateOrThrow(msgObject, plan.PayloadType, plan.MatchedSubject, logger);
        }
        catch (NatsMessageValidationException validationFailure)
        {
            return await MapValidationFailureAsync(
                plan,
                msgObject,
                validationFailure,
                rawData,
                jetStream,
                exceptionHandlers,
                logger,
                ct).ConfigureAwait(false);
        }

        return await InvokeAsync(plan, msgObject, rawData, jetStream, exceptionHandlers, logger, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Routes a failed payload validation through the registered exception handlers.
    /// </summary>
    /// <returns>The ack to apply, or <c>null</c> when no handler claimed the failure.</returns>
    private static async ValueTask<NatsAck?> MapValidationFailureAsync(
        NatsConsumerInvocationPlan plan,
        object msgObject,
        NatsMessageValidationException validationFailure,
        byte[]? rawData,
        NatsConsumerJetStreamMetadata? jetStream,
        IReadOnlyList<INatsConsumerExceptionHandler> exceptionHandlers,
        ILogger logger,
        CancellationToken ct)
    {
        // Hand handlers what the schema threw, not the platform wrapper.
        var exception = Unwrap(validationFailure.InnerException ?? validationFailure);

        var ack = await ExecuteExceptionHandlersAsync(
            plan,
            msgObject,
            exception,
            rawData,
            jetStream,
            exceptionHandlers,
            logger,
            isValidationFailure: true,
            ct).ConfigureAwait(false);

        if (ack is null)
        {
            // Unchanged behaviour: the message is discarded. Kept at Error, with the same wording,
            // because this is the line services alert on today.
            logger.LogError(
                validationFailure,
                "Message validation failed for subject {Subject} with payload type {PayloadType}. Discarding message.",
                plan.MatchedSubject,
                plan.PayloadType.Name);

            return null;
        }

        // A payload that failed validation cannot become valid by being redelivered, so a nak would
        // loop until the stream's MaxDeliver gives up. Terminate instead, and say why.
        if (jetStream is not null && !ack.IsAcknowledged && ack.ShouldRetryDelivery)
        {
            logger.LogWarning(
                validationFailure,
                "Exception handler {ExceptionHandler} asked for redelivery of a message that failed validation on subject {Subject}. "
                + "Redelivery cannot make it valid, so the message is terminated instead.",
                ack.MappedByHandlerType?.FullName,
                plan.MatchedSubject);

            return new NatsAck(
                new NatsAck(_isAck: false, _reply: ack.Reply, _opts: ack.Opts, _shouldRetryDelivery: false),
                exception,
                ack.MappedByHandlerType!);
        }

        return ack;
    }

    /// <summary>
    /// Invokes the handler described by <paramref name="plan"/> and returns its <see cref="NatsAck"/>.
    /// On failure, runs exception handlers in order until one returns a non-null ack.
    /// </summary>
    /// <param name="plan">The compiled invocation plan for the matched subject.</param>
    /// <param name="msgObject">The typed message wrapper passed to the handler.</param>
    /// <param name="rawData">Original serialized message bytes.</param>
    /// <param name="jetStream">JetStream delivery information, or <c>null</c> for Core NATS messages.</param>
    /// <param name="exceptionHandlers">Registered exception handlers in DI order.</param>
    /// <param name="logger">Logger used to record mapped exceptions and misbehaving exception handlers.</param>
    /// <param name="ct">Cancellation token passed to the handler and exception handlers.</param>
    /// <returns>
    /// The handler ack, or an ack produced by an exception handler. An ack produced by an exception
    /// handler carries <see cref="NatsAck.IsExceptionMapped"/> so the caller can record the
    /// invocation as a failure while still applying the ack semantics the handler asked for.
    /// </returns>
    /// <exception cref="OperationCanceledException">Propagated when cancellation is requested.</exception>
    /// <exception cref="Exception">Re-thrown when no exception handler produces an ack.</exception>
    internal static async Task<NatsAck> InvokeAsync(
        NatsConsumerInvocationPlan plan,
        object msgObject,
        byte[]? rawData,
        NatsConsumerJetStreamMetadata? jetStream,
        IReadOnlyList<INatsConsumerExceptionHandler> exceptionHandlers,
        ILogger logger,
        CancellationToken ct)
    {
        try
        {
            return await plan.Invoke(msgObject, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            var exception = Unwrap(ex);

            // Do not convert cooperative cancellation into a NatsAck.
            if (exception is OperationCanceledException && ct.IsCancellationRequested)
                ExceptionDispatchInfo.Capture(exception).Throw();

            var handled = await ExecuteExceptionHandlersAsync(
                plan,
                msgObject,
                exception,
                rawData,
                jetStream,
                exceptionHandlers,
                logger,
                isValidationFailure: false,
                ct).ConfigureAwait(false);

            if (handled is not null)
                return handled;

            // Preserve the original stack trace instead of resetting it with `throw exception`.
            ExceptionDispatchInfo.Capture(exception).Throw();
            throw;
        }
    }

    /// <summary>
    /// Runs registered exception handlers in registration order until one returns a non-null ack.
    /// </summary>
    /// <remarks>
    /// Each handler is isolated: if a handler itself throws, the failure is logged and the next
    /// handler runs. Letting it escape here would replace the original handler exception and lose
    /// the actual reason the message failed.
    /// </remarks>
    private static async ValueTask<NatsAck?> ExecuteExceptionHandlersAsync(
        NatsConsumerInvocationPlan plan,
        object msgObject,
        Exception exception,
        byte[]? rawData,
        NatsConsumerJetStreamMetadata? jetStream,
        IReadOnlyList<INatsConsumerExceptionHandler> exceptionHandlers,
        ILogger logger,
        bool isValidationFailure,
        CancellationToken ct)
    {
        if (exceptionHandlers.Count == 0)
            return null;

        var context = new NatsConsumerExceptionContext(
            plan.MatchedSubject,
            plan.PayloadType,
            plan.HandlerType,
            plan.HandlerMethod,
            msgObject,
            exception,
            rawData,
            jetStream,
            isValidationFailure);

        foreach (var handler in exceptionHandlers)
        {
            NatsAck? ack;
            try
            {
                ack = await handler.HandleExceptionAsync(context, ct).ConfigureAwait(false);
            }
            catch (Exception handlerException)
            {
                logger.LogError(
                    handlerException,
                    "NATS consumer exception handler {ExceptionHandler} threw while mapping {OriginalExceptionType} from {HandlerType}.{HandlerName} for subject {Subject}. Continuing with the next exception handler.",
                    handler.GetType().FullName,
                    exception.GetType().FullName,
                    plan.HandlerType.Name,
                    plan.HandlerName,
                    plan.MatchedSubject);
                continue;
            }

            if (ack is null)
                continue;

            if (isValidationFailure)
            {
                // The handler never ran, so do not report it as the thing that threw.
                logger.LogWarning(
                    exception,
                    "Payload validation for {HandlerType}.{HandlerName} threw {ExceptionType} for subject {Subject}. Exception handler {ExceptionHandler} mapped it to isAck={IsAcknowledged} shouldRetryDelivery={ShouldRetryDelivery} hasReply={HasReply}.",
                    plan.HandlerType.Name,
                    plan.HandlerName,
                    exception.GetType().FullName,
                    plan.MatchedSubject,
                    handler.GetType().FullName,
                    ack.IsAcknowledged,
                    ack.ShouldRetryDelivery,
                    ack.Reply is not null);
            }
            else
            {
                logger.LogWarning(
                    exception,
                    "NATS consumer handler {HandlerType}.{HandlerName} threw {ExceptionType} for subject {Subject}. Exception handler {ExceptionHandler} mapped it to isAck={IsAcknowledged} shouldRetryDelivery={ShouldRetryDelivery} hasReply={HasReply}.",
                    plan.HandlerType.Name,
                    plan.HandlerName,
                    exception.GetType().FullName,
                    plan.MatchedSubject,
                    handler.GetType().FullName,
                    ack.IsAcknowledged,
                    ack.ShouldRetryDelivery,
                    ack.Reply is not null);
            }

            return new NatsAck(ack, exception, handler.GetType());
        }

        return null;
    }

    /// <summary>
    /// Unwraps reflection and aggregate wrappers so exception handlers see the original fault.
    /// </summary>
    private static Exception Unwrap(Exception ex)
    {
        if (ex is TargetInvocationException { InnerException: { } innerTie })
            return Unwrap(innerTie);

        if (ex is AggregateException { InnerExceptions.Count: 1 } agg)
            return Unwrap(agg.InnerExceptions[0]);

        return ex;
    }
}
