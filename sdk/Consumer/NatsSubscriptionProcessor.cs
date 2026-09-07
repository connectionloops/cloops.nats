using System.Reflection;
using CLOOPS.NATS;
using CLOOPS.NATS.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NATS.Client.Core;
using NATS.Client.JetStream;
using CLOOPS.NATS.Meta;
using System.Diagnostics;
using System.Net;
using System.Text.RegularExpressions;

/// <summary>
/// Exception thrown when message validation fails
/// </summary>
internal class MessageValidationException : Exception
{
    public MessageValidationException(string message) : base(message) { }
    public MessageValidationException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>
/// The Processor class thats sets up subcription and processor of incoming messages
/// </summary>
internal class NatsSubscriptionProcessor
{
    // Effective queue group and consumer id
    private readonly string consumerId;
    private readonly NatsSubscriptionQueue queue;
    private readonly CloopsNatsClient client;

    private readonly IServiceProvider sp;

    private readonly ILogger<NatsSubscriptionProcessor> logger;

    private readonly SemaphoreSlim _concurrencyLimiter;

    /** Subject Specific Variables */
    private readonly Dictionary<string, NatsConsumerAttribute> nca;
    private readonly Dictionary<string, MethodInfo> handler;
    private readonly Dictionary<string, Type> handlerClassType;

    private readonly Dictionary<string, object> handlerClassInstance;

    private readonly List<string> Subjects;
    private readonly int MaxDOP = 128;

    /// <summary>
    /// Capacity of the in-memory work queue between the subscription and the workers.
    /// Defaults to 2 x <see cref="MaxDOP"/>; override with <c>NATS_SUBSCRIPTION_QUEUE_SIZE</c>.
    /// </summary>
    internal int QueueCapacity { get; }

    /// <summary>
    /// When true (the default), JetStream ack / nak / terminate calls that the handler did not
    /// configure explicitly are sent with <c>DoubleAck</c>, i.e. they wait for the server to confirm
    /// it recorded the ack. Disable with <c>NATS_CONSUMER_DOUBLE_ACK=false</c>.
    /// </summary>
    internal bool DoubleAckByDefault { get; }

    private bool UseBatching = false;
    private int BatchTimeoutMs = 100;

    private readonly bool IsDurable = false;

    private Dictionary<string, Type> PayloadTypeCache = new();

    // Per-subject compiled invocation plans, built once during Setup.
    private readonly Dictionary<string, NatsConsumerInvocationPlan> plans = new();

    // Cache for Validate() method per payload type to avoid reflection overhead
    private readonly Dictionary<Type, MethodInfo?> ValidateMethodCache = new();

    // Cache for Data property getter per message wrapper type to avoid reflection overhead
    private readonly Dictionary<Type, PropertyInfo?> DataPropertyCache = new();

    private NatsSubjectMatcher? subjectMatcher;

    private readonly INatsMetricsService? metricsService;

    private readonly IReadOnlyList<INatsConsumerInterceptor> consumerInterceptors;

    private readonly IReadOnlyList<INatsConsumerExceptionHandler> consumerExceptionHandlers;

    /// <summary>
    /// Initializes a new instance of <see cref="NatsSubscriptionProcessor"/> responsible for
    /// invoking a discovered NATS consumer handler method when messages arrive.
    /// </summary>
    /// <param name="_sp">The application <see cref="IServiceProvider"/> used to resolve the handler class instance.</param>
    /// <param name="_client">The configured <see cref="CloopsNatsClient"/> for interacting with NATS.</param>
    /// <param name="_consumerId">The consumer id to use for this subscription</param>
    /// <param name="_IsDurable">Is this a durable consumer</param>
    /// <remarks>
    /// The handler method is expected to accept exactly two parameters: a typed message wrapper and a <see cref="CancellationToken"/>.
    /// If the handler returns a <see cref="Task"/>, it is awaited; otherwise it is treated as synchronous.
    /// Exceptions thrown by handlers are routed to registered <see cref="INatsConsumerExceptionHandler"/> instances
    /// (if any) so they can return a <see cref="NatsAck"/> instead of faulting the work item.
    /// </remarks>
    internal NatsSubscriptionProcessor(
        IServiceProvider _sp,
        CloopsNatsClient _client,
        string _consumerId,
        bool _IsDurable = false
    )
    {
        handler = new();
        nca = new();
        handlerClassType = new();
        handlerClassInstance = new();
        Subjects = new();

        client = _client;
        sp = _sp;
        logger = sp.GetRequiredService<ILogger<NatsSubscriptionProcessor>>();
        IsDurable = _IsDurable;
        metricsService = sp.GetService<INatsMetricsService>();
        consumerInterceptors = sp.GetServices<INatsConsumerInterceptor>().ToArray();
        consumerExceptionHandlers = sp.GetServices<INatsConsumerExceptionHandler>().ToArray();

        MaxDOP = int.TryParse(Environment.GetEnvironmentVariable("NATS_CONSUMER_MAX_DOP"), out int _maxDop) ? _maxDop : 128;

        // The queue only exists to keep the workers fed between batch reads, so it is sized relative
        // to the worker pool rather than as a large absolute number. A deep queue silently adds
        // delivery-to-ack latency (depth x avg handler time / MaxDOP) and once that exceeds the
        // consumer's AckWait the server redelivers messages that are still waiting in memory.
        QueueCapacity = int.TryParse(Environment.GetEnvironmentVariable("NATS_SUBSCRIPTION_QUEUE_SIZE"), out int queueSize) ? queueSize : MaxDOP * 2;
        queue = new NatsSubscriptionQueue(QueueCapacity);
        _concurrencyLimiter = new SemaphoreSlim(MaxDOP, MaxDOP);
        consumerId = _consumerId;

        // Double-ack by default: without it, awaiting an ack only means it reached the client's send
        // buffer, so a crash or dropped connection right after a handler completes loses the ack and
        // the message is processed again after AckWait. DoubleAck asks the server to confirm it
        // recorded the ack, at the cost of one round trip per message (paid inside the parallel
        // worker pool). A handler that sets AckOpts.DoubleAck itself always wins.
        DoubleAckByDefault = !bool.TryParse(Environment.GetEnvironmentVariable("NATS_CONSUMER_DOUBLE_ACK"), out bool doubleAck) || doubleAck;

    }

    #region bootstreap

    /// <summary>
    /// The consumer id this processor subscribes with.
    /// </summary>
    internal string RegisteredConsumerId => consumerId;

    /// <summary>
    /// Subjects bound to this processor, in registration order.
    /// </summary>
    internal IReadOnlyList<string> RegisteredSubjects => Subjects;

    /// <summary>
    /// Handler method bound to each subject.
    /// </summary>
    internal IReadOnlyDictionary<string, MethodInfo> RegisteredHandlers => handler;

    public void AddSubject(string subject, NatsConsumerAttribute _nca, Type _handlerClassType, MethodInfo _handler)
    {
        nca.Add(subject, _nca);
        handler.Add(subject, _handler);
        handlerClassType.Add(subject, _handlerClassType);
        var hcInstance = sp.GetRequiredService(_handlerClassType);
        handlerClassInstance.Add(subject, hcInstance);
        Subjects.Add(subject);

    }

    /// <summary>
    /// Setup batching constructs
    /// </summary>
    /// <param name="_UseBatching">Bool</param>
    /// <param name="_BatchTimeoutMs">How long to wait until a batch is formed</param>
    public void SetupBatching(bool _UseBatching = true, int _BatchTimeoutMs = 100)
    {
        UseBatching = _UseBatching;
        BatchTimeoutMs = _BatchTimeoutMs;
    }
    /// <summary>
    /// Validates every bound handler signature and compiles its invoker.
    /// </summary>
    /// <remarks>
    /// Split out of <see cref="Setup"/> so the binding can be validated and exercised without opening a
    /// NATS subscription. Throws for any handler that does not match the consumer contract.
    /// </remarks>
    internal void BuildInvocationPlans()
    {
        // validation
        foreach (string subject in Subjects)
        {
            // populates cache and performs validations.
            var payloadType = GetPayloadType(subject);

            // Compile the handler invoker once, after the signature has been validated.
            plans[subject] = new NatsConsumerInvocationPlan(
                subject,
                payloadType,
                handlerClassType[subject],
                handler[subject],
                handlerClassInstance[subject]);
        }
    }

    /// <summary>
    /// The compiled invocation plan bound to a subject. Call <see cref="BuildInvocationPlans"/> first.
    /// </summary>
    internal NatsConsumerInvocationPlan GetInvocationPlan(string subject) => plans[subject];

    #endregion bootstrap

    #region setup
    /// <summary>
    /// Starts both the background processing loop and the NATS subscription listener
    /// and waits for them to complete. Completion normally occurs when the provided
    /// <paramref name="ct"/> is cancelled, triggering a graceful shutdown of queued work.
    /// </summary>
    /// <param name="ct">A cancellation token used to signal shutdown of listening and processing loops.</param>
    /// <remarks>
    /// This method composes two cooperating loops:
    /// <list type="number">
    /// <item><description><see cref="SetupProcessor"/> which dequeues and executes work items with concurrency / batching.</description></item>
    /// <item><description><see cref="Listen"/> which listens to the NATS subject and enqueues work items.</description></item>
    /// </list>
    /// Running them together ensures continuous flow from subscription to execution while respecting throttling.
    /// </remarks>
    internal async Task Setup(CancellationToken ct)
    {
        BuildInvocationPlans();

        // subject matcher
        subjectMatcher = new NatsSubjectMatcher(Subjects);

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var t1 = SetupProcessor(linkedCts.Token);
        var t2 = Listen(linkedCts.Token);

        // Wait for any task to finish (fault, cancel, or complete)
        var firstFinished = await Task.WhenAny(t1, t2).ConfigureAwait(false);

        if (ct.IsCancellationRequested)
        {
            linkedCts.Cancel();
            try { await Task.WhenAll(t1, t2).ConfigureAwait(false); } catch { }
            return;
        }

        if (firstFinished.IsFaulted)
        {
            linkedCts.Cancel();

            // Aggregate & log all exceptions from both once they've settled
            try { await Task.WhenAll(t1, t2).ConfigureAwait(false); }
            catch
            {
                var agg = Task.WhenAll(t1, t2).Exception ?? firstFinished.Exception;
                // some one faulted, log all exceptions
                if (agg != null)
                {
                    foreach (var ex in agg.Flatten().InnerExceptions)
                    {
                        if (ex is OperationCanceledException) continue;
                        logger.LogError(ex, "Subscription processor for consumer id {consumerId} faulted", consumerId);
                    }
                }
                Environment.FailFast("CLOOPS NATS Subscription Failed. See the log messages");
            }
            return; // (Should not reach here because of throw above, but for clarity.)
        }

        // If one completed normally (unexpected because these are typically long-running loops), cancel the other.
        if (firstFinished.IsCompletedSuccessfully)
        {
            linkedCts.Cancel();
            try { await Task.WhenAll(t1, t2).ConfigureAwait(false); }
            catch (Exception ex)
            {
                logger.LogError(ex, "Subscription processor for consumer id {consumerId} faulted during shutdown after early completion", consumerId);
                throw;
            }
        }
    }

    /// <summary>
    /// Continuously dequeues work items from the internal <see cref="NatsSubscriptionQueue"/> and
    /// executes them subject to the concurrency and optional batching constraints specified by
    /// the associated <see cref="NatsConsumerAttribute"/>.
    /// </summary>
    /// <param name="ct">Cancellation token that stops the worker loop and initiates graceful shutdown.</param>
    /// <remarks>
    /// The loop:
    /// <list type="bullet">
    /// <item><description>Reads up to <c>maxDOP</c> work items per batch if batching is enabled; otherwise one at a time.</description></item>
    /// <item><description>Tracks running tasks so it can await their completion during shutdown.</description></item>
    /// <item><description>Uses a <see cref="SemaphoreSlim"/> to enforce the maximum degree of parallelism.</description></item>
    /// </list>
    /// Exceptions thrown by individual work items are logged and allowed to fault their task; the processor loop itself
    /// catches unexpected exceptions, logs them, waits briefly, and continues, providing resilience against transient errors.
    /// </remarks>
    private async Task SetupProcessor(CancellationToken ct)
    {
        logger.LogInformation("Setting up worker for consumer id {consumerId} with DOP: {DOP}", consumerId, MaxDOP);

        var runningTasks = new HashSet<Task>();
        int batchSize = UseBatching ? MaxDOP : 1;
        TimeSpan batchTimeoutTS = TimeSpan.FromMilliseconds(BatchTimeoutMs);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                // Clean up completed tasks
                runningTasks.RemoveWhere(task => task.IsCompleted);

                // Read a batch of work items with a timeout
                var workItems = await queue.ReadAsync(
                    batchSize,
                    batchTimeoutTS,
                    ct).ConfigureAwait(false);


                // Process each work item in parallel
                foreach (var workItem in workItems)
                {
                    // Wait for a slot to become available
                    await _concurrencyLimiter.WaitAsync(ct).ConfigureAwait(false);

                    // Create and start the task
                    var task = ProcessWorkItemAsync(workItem, ct);
                    runningTasks.Add(task);

                    // Fire and forget - don't await here to allow rolling execution
                    _ = task.ContinueWith(_ => _concurrencyLimiter.Release(), TaskContinuationOptions.None);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                logger.LogInformation("Worker with consumer id {consumerId} shutting down", consumerId);
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error occurred in background task service main loop.");
                await Task.Delay(1000, ct).ConfigureAwait(false); // Wait before retrying
            }
        }

        // Wait for all running tasks to complete during shutdown
        if (runningTasks.Count > 0)
        {
            logger.LogInformation("Worker {consumerId} waiting for {Count} running tasks to complete during shutdown.", consumerId, runningTasks.Count);
            await Task.WhenAll(runningTasks).ConfigureAwait(false);
        }

        logger.LogInformation("Worker for consumer id {consumerid} stopped", consumerId);
    }

    /// <summary>
    /// Begins listening for messages on the configured subject and dispatches each message
    /// to the handler method on a background work queue until cancellation is requested.
    /// </summary>
    /// <param name="ct">A <see cref="CancellationToken"/> used to stop listening and processing.</param>
    /// <exception cref="InvalidOperationException">
    /// Thrown if the handler method does not define exactly two parameters (payload, CancellationToken)
    /// or if the payload parameter does not specify exactly one generic type argument.
    /// </exception>
    /// <remarks>
    /// The method subscribes using the generic SubscribeAsync overload for a raw byte[] payload,
    /// constructs a strongly-typed wrapper object and queues execution.
    /// Any asynchronous handler is awaited to completion before the work item finishes.
    /// </remarks>
    private async Task Listen(CancellationToken ct)
    {
        if (IsDurable)
        {
            var durableSubscription = await GetJsSubscriptionAsync(ct).ConfigureAwait(false);
            await foreach (var m in durableSubscription)
            {
                var subject = subjectMatcher?.Match(m.Subject);
                if (subject is null)
                {
                    // Leave the message unacked so JetStream redelivers it and the stream's own
                    // MaxDeliver / DLQ policy takes over. Terminating here would silently discard
                    // messages whenever the consumer's filter subjects drift ahead of the handlers.
                    logger.LogError(
                        "Can't match {Subject} to a subscribed subject for consumer id {consumerId}. Your consumer is subscribed to more subjects than you have handlers. Leaving the message unacked for redelivery.",
                        m.Subject, consumerId);
                    continue;
                }

                var plan = plans[subject];
                try
                {
                    await EnqueueHandlerInvocationAsync(m, plan, ct).ConfigureAwait(false);
                }
                catch (MessageValidationException ex)
                {
                    // Validation failed - terminate and discard the message (don't retry)
                    try
                    {
                        await m.AckTerminateAsync(EffectiveAckOpts(null), cancellationToken: ct).ConfigureAwait(false);
                    }
                    catch (Exception ackEx)
                    {
                        logger.LogWarning(ackEx, "Failed to terminate message after validation failure for subject {Subject}", subject);
                    }
                    logger.LogError(ex, "Message validation failed for subject {Subject} with payload type {PayloadType}. Discarding message.", subject, plan.PayloadType.Name);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Can't process message {Subject} with payload type {PayloadType}. Most likely the message is not of type {PayloadType}. Skipping the message {Message}",
                        subject, plan.PayloadType.Name, plan.PayloadType.Name, System.Text.Encoding.UTF8.GetString(m.Data ?? Array.Empty<byte>()));
                }
            }

        }
        else
        {
            var coreSubscription = GetCoreSubscription(ct);
            var subject = Subjects.First();
            var plan = plans[subject];
            await foreach (var m in coreSubscription.ConfigureAwait(false))
            {
                try
                {
                    await EnqueueHandlerInvocationAsync(m, plan, ct).ConfigureAwait(false);
                }
                catch (MessageValidationException ex)
                {
                    // Validation failed - discard the message (no ack needed for core NATS)
                    logger.LogError(ex, "Message validation failed for subject {Subject} with payload type {PayloadType}. Discarding message.", subject, plan.PayloadType.Name);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Can't process message {Subject} | {originalSubject} with payload type {PayloadType}. Most likely the message is not of type {PayloadType}. Skipping the message {Message}",
                        subject, m.Subject, plan.PayloadType.Name, plan.PayloadType.Name, System.Text.Encoding.UTF8.GetString(m.Data ?? Array.Empty<byte>()));
                }
            }

        }
    }
    #endregion

    #region processing-messages

    /// <summary>
    /// Enqueue handler invocation for JetStream message (applies Ack/Nak based on handler result).
    /// Note: only one subscription exists per consumer id, so multiple subjects are handled by the same subscription.
    /// This is why we need to use the subject to get the handler and handler class instance.
    /// </summary>
    private async ValueTask EnqueueHandlerInvocationAsync(NatsJSMsg<byte[]> rawMsg, NatsConsumerInvocationPlan plan, CancellationToken ct)
    {
        var msgObject = BaseNatsUtil.CreateTypedMsgWrapper(rawMsg, plan.PayloadType);
        var jetStream = NatsConsumerJetStreamMetadata.From(rawMsg);

        var interception = await ExecuteConsumerInterceptorsAsync(
            plan,
            msgObject,
            rawMsg.Data,
            jetStream,
            ct).ConfigureAwait(false);

        if (!interception.result.ShouldContinue)
        {
            await RejectJetStreamMessageAsync(rawMsg, plan.MatchedSubject, interception.result, ct).ConfigureAwait(false);
            return;
        }

        msgObject = interception.message;
        var rawData = rawMsg.Data;

        // Validate message payload if it has a Validate() method
        ValidateMessageOrThrow(msgObject, plan.PayloadType, plan.MatchedSubject);

        await queue.QueueBackgroundWorkItem(new WorkItem(plan.MatchedSubject, async token =>
        {
            var ackResult = await NatsConsumerHandlerInvoker.InvokeAsync(
                plan,
                msgObject,
                rawData,
                jetStream,
                consumerExceptionHandlers,
                logger,
                token).ConfigureAwait(false);

            if (ackResult.IsAcknowledged)
            {
                await rawMsg.AckAsync(EffectiveAckOpts(ackResult.Opts), cancellationToken: token).ConfigureAwait(false);

                // An exception handler chose to ack, but the invocation still failed. Apply the ack
                // it asked for while reporting the failure so error rates stay accurate.
                return (ackResult.IsExceptionMapped ? WorkItemExecutionStatus.FAIL : WorkItemExecutionStatus.SUCCESS, false);
            }
            else if (!ackResult.ShouldRetryDelivery)
            {
                await rawMsg.AckTerminateAsync(EffectiveAckOpts(ackResult.Opts), cancellationToken: token).ConfigureAwait(false);
                return (WorkItemExecutionStatus.FAIL, false);
            }
            else
            {
                await rawMsg.NakAsync(EffectiveAckOpts(ackResult.Opts), cancellationToken: token).ConfigureAwait(false);
                return (WorkItemExecutionStatus.FAIL, true);
            }
        }
        )).ConfigureAwait(false);
    }
    /// <summary>
    /// Enqueue handler invocation for Core message (no acks, but same Task&lt;NatsAck&gt; contract).
    /// Since this is nats core message, we actually don't need to handle subject -> handler mapping, there is only one subject per consumer, ie.e. each subject and its handler belongs to one consumer no multiplexing
    /// </summary>
    private async ValueTask EnqueueHandlerInvocationAsync(NatsMsg<byte[]> rawMsg, NatsConsumerInvocationPlan plan, CancellationToken ct)
    {
        var msgObject = BaseNatsUtil.CreateTypedMsgWrapper(rawMsg, plan.PayloadType);

        // Core NATS carries no JetStream delivery information.
        var interception = await ExecuteConsumerInterceptorsAsync(
            plan,
            msgObject,
            rawMsg.Data,
            jetStream: null,
            ct).ConfigureAwait(false);

        if (!interception.result.ShouldContinue)
        {
            await RejectCoreMessageAsync(rawMsg, plan.MatchedSubject, interception.result, ct).ConfigureAwait(false);
            return;
        }

        msgObject = interception.message;
        var rawData = rawMsg.Data;

        // Validate message payload if it has a Validate() method
        ValidateMessageOrThrow(msgObject, plan.PayloadType, plan.MatchedSubject);

        await queue.QueueBackgroundWorkItem(new WorkItem(plan.MatchedSubject, async token =>
        {
            var ackResult = await NatsConsumerHandlerInvoker.InvokeAsync(
                plan,
                msgObject,
                rawData,
                jetStream: null,
                consumerExceptionHandlers,
                logger,
                token).ConfigureAwait(false);

            if (ackResult.Reply != null && rawMsg.ReplyTo != null)
                await rawMsg.ReplyAsync(ackResult.Reply, cancellationToken: token).ConfigureAwait(false);

            // There is no redelivery in Core NATS, so ack/nak semantics do not apply. The invocation
            // is a success unless an exception handler produced this ack from a thrown exception.
            return (ackResult.IsExceptionMapped ? WorkItemExecutionStatus.FAIL : WorkItemExecutionStatus.SUCCESS, false);
        }
        )).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs registered consumer interceptors in dependency injection registration order.
    /// </summary>
    private async ValueTask<(NatsConsumerInterceptorResult result, object message)> ExecuteConsumerInterceptorsAsync(
        NatsConsumerInvocationPlan plan,
        object msgObject,
        byte[]? rawData,
        NatsConsumerJetStreamMetadata? jetStream,
        CancellationToken ct)
    {
        if (consumerInterceptors.Count == 0)
            return (NatsConsumerInterceptorResult.Continue(), msgObject);

        var context = new NatsConsumerInterceptorContext(
            plan.MatchedSubject, plan.PayloadType, plan.HandlerType, plan.HandlerMethod, msgObject, rawData, jetStream);
        foreach (var interceptor in consumerInterceptors)
        {
            var result = await interceptor.InterceptAsync(context, ct).ConfigureAwait(false);
            if (result is null)
            {
                throw new InvalidOperationException(
                    $"Consumer interceptor {interceptor.GetType().FullName} returned null for subject {plan.MatchedSubject}.");
            }

            if (!result.ShouldContinue)
                return (result, context.Message);
        }

        return (NatsConsumerInterceptorResult.Continue(), context.Message);
    }

    /// <summary>
    /// Rejects a JetStream message according to the interceptor result.
    /// </summary>
    private async Task RejectJetStreamMessageAsync(
        NatsJSMsg<byte[]> rawMsg,
        string subject,
        NatsConsumerInterceptorResult result,
        CancellationToken ct)
    {
        LogRejectedMessage(subject, result);

        // JetStream rejects only affect delivery/ack semantics. Optional Reply is ignored here.
        if (result.ShouldRetryDelivery)
        {
            await rawMsg.NakAsync(EffectiveAckOpts(null), cancellationToken: ct).ConfigureAwait(false);
            return;
        }

        await rawMsg.AckTerminateAsync(EffectiveAckOpts(null), cancellationToken: ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Rejects a Core NATS message according to the interceptor result.
    /// When the reject includes a reply and the inbound message has a ReplyTo, the reply is sent.
    /// </summary>
    private async Task RejectCoreMessageAsync(
        NatsMsg<byte[]> rawMsg,
        string subject,
        NatsConsumerInterceptorResult result,
        CancellationToken ct)
    {
        LogRejectedMessage(subject, result);

        if (result.Reply != null && rawMsg.ReplyTo != null)
        {
            await rawMsg.ReplyAsync(result.Reply, cancellationToken: ct).ConfigureAwait(false);
            return;
        }

        if (result.Reply != null && rawMsg.ReplyTo == null)
        {
            logger.LogWarning(
                "NATS consumer interceptor rejected subject {Subject} with a reply payload, but the inbound message has no ReplyTo. Reply was not sent.",
                subject);
        }
    }

    /// <summary>
    /// Logs a message rejected by a consumer interceptor before handler invocation.
    /// </summary>
    private void LogRejectedMessage(string subject, NatsConsumerInterceptorResult result)
    {
        logger.LogWarning(
            "NATS consumer interceptor rejected message for subject {Subject}. shouldRetryDelivery={ShouldRetryDelivery} hasReply={HasReply} reason={Reason}",
            subject,
            result.ShouldRetryDelivery,
            result.Reply is not null,
            result.Reason ?? "");
    }

    /// <summary>
    /// Executes a single queued work item with error logging.
    /// </summary>
    private async Task ProcessWorkItemAsync(WorkItem workItem, CancellationToken stoppingToken)
    {
        string fn = plans[workItem.Subject].HandlerName;
        Stopwatch sw = Stopwatch.StartNew();
        string executionStatus = WorkItemExecutionStatus.FAIL; // assume worst
        bool isRetryable = false; // assume worst
        try
        {
            var result = await workItem.Fn(stoppingToken).ConfigureAwait(false);
            executionStatus = result.status;
            isRetryable = result.isRetryable;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error occurred while executing a background task for subject {Subject}.", workItem.Subject);
            isRetryable = true; // unknown exception. might work next time. No ack is recorded, so it is retryable.
            throw;
        }
        finally
        {
            sw.Stop();
            logger.LogDebug("Recording execution metrics. metricsServiceDefined={metricsServiceDefined} Duration_ms={su} Fn={fn}, execution status={es}, isRetryable={ir}", metricsService is not null, sw.ElapsedMilliseconds, fn, executionStatus, isRetryable);
            metricsService?.RecordNatsSubMsgProcessMs(sw.ElapsedMilliseconds, fn, executionStatus, isRetryable);
        }
    }

    #endregion

    #region utility-functions

    /// <summary>
    /// Applies the double-ack default to the options a handler returned (or to none at all).
    /// </summary>
    /// <remarks>
    /// A handler that set <see cref="AckOpts.DoubleAck"/> explicitly - to true or to false - always
    /// wins. Only when it left the decision open (no opts, or opts without <c>DoubleAck</c>) does the
    /// processor-wide default apply. Used for every JetStream ack, nak and terminate this processor
    /// sends; core NATS has no acks.
    /// </remarks>
    internal AckOpts? EffectiveAckOpts(AckOpts? opts)
    {
        if (opts?.DoubleAck is not null || !DoubleAckByDefault)
        {
            return opts;
        }

        return (opts ?? default) with { DoubleAck = true };
    }

    /// <summary>
    /// Finds the first (and by policy, only) stream that captures the given subject and returns its name.
    /// Throws if no stream matches.
    /// </summary>
    private static async Task<string> GetMatchingFirstStreamNameAsync(
        INatsJSContext js,
        string subject,
        CancellationToken ct)
    {
        await foreach (var name in js.ListStreamNamesAsync(subject: subject, cancellationToken: ct).ConfigureAwait(false))
        {
            return name;
        }

        throw new InvalidOperationException(
            $"No JetStream stream found that matches subject '{subject}'. " +
            "Streams are managed in the control plane; please ensure one is configured for this subject.");
    }

    /// <summary>
    /// Durable JetStream subscription (resolves stream via subject lookup, control plane owns consumer).
    /// </summary>
    private async Task<IAsyncEnumerable<NatsJSMsg<byte[]>>> GetJsSubscriptionAsync(CancellationToken ct)
    {
        HashSet<string> matchingStreamNames = new();
        foreach (string s in Subjects)
        {
            // Resolve the stream name for this subject (no hardcoded stream)
            var streamName = await GetMatchingFirstStreamNameAsync(client.JsContext, s, ct).ConfigureAwait(false);
            matchingStreamNames.Add(streamName);

            logger.LogInformation(
            "Subscribed to {Subject} with durable consumer {ConsumerId} on stream {Stream}",
            s, consumerId, streamName);
        }

        if (matchingStreamNames.Count > 1)
        {
            // we have multiple streams matching for this consumer.
            throw new InvalidOperationException($"The subjects given for consumer id {consumerId} are matching multiple streams. This is an invalid configuration. Exiting ...");
        }

        // Attach to existing consumer (managed by GitOps)
        var consumer = await client.JsContext.GetConsumerAsync(
            stream: matchingStreamNames.First(),
            consumer: consumerId,
            cancellationToken: ct
        ).ConfigureAwait(false);

        var jsSubscription = consumer.ConsumeAsync<byte[]>(cancellationToken: ct);


        return jsSubscription;
    }

    /// <summary>
    /// Resolves placeholders in queue group names to support runtime values.
    /// Supported placeholders:
    /// - {POD_NAME} - resolves to POD_NAME env var, or HOSTNAME, or machine name
    /// - {HOSTNAME} - resolves to HOSTNAME env var, or machine name
    /// - {MACHINE_NAME} - resolves to machine name
    /// - {ENV:VAR_NAME} - resolves to any environment variable
    /// </summary>
    private string ResolveQueueGroupPlaceholders(string queueGroupName)
    {
        if (string.IsNullOrEmpty(queueGroupName))
            return queueGroupName;

        string resolved = queueGroupName;

        // Resolve {POD_NAME} - try POD_NAME env var first, then HOSTNAME, then machine name
        if (resolved.Contains("{POD_NAME}"))
        {
            var podName = Environment.GetEnvironmentVariable("POD_NAME")
                ?? Environment.GetEnvironmentVariable("HOSTNAME")
                ?? Dns.GetHostName();
            resolved = resolved.Replace("{POD_NAME}", podName);
        }

        // Resolve {HOSTNAME} - try HOSTNAME env var first, then machine name
        if (resolved.Contains("{HOSTNAME}"))
        {
            var hostname = Environment.GetEnvironmentVariable("HOSTNAME")
                ?? Dns.GetHostName();
            resolved = resolved.Replace("{HOSTNAME}", hostname);
        }

        // Resolve {MACHINE_NAME} - use machine name
        if (resolved.Contains("{MACHINE_NAME}"))
        {
            var machineName = Dns.GetHostName();
            resolved = resolved.Replace("{MACHINE_NAME}", machineName);
        }

        // Resolve {ENV:VAR_NAME} pattern
        var envVarPattern = Regex.Match(resolved, @"\{ENV:([^}]+)\}");
        while (envVarPattern.Success)
        {
            var envVarName = envVarPattern.Groups[1].Value;
            var envVarValue = Environment.GetEnvironmentVariable(envVarName) ?? "";
            resolved = resolved.Replace($"{{ENV:{envVarName}}}", envVarValue);
            envVarPattern = envVarPattern.NextMatch();
        }

        return resolved;
    }

    /// <summary>
    /// Non-durable Core NATS subscription.
    /// </summary>
    private IAsyncEnumerable<NatsMsg<byte[]>> GetCoreSubscription(CancellationToken ct)
    {
        var subject = Subjects.First();
        var nca = this.nca[subject];

        // Resolve placeholders in queue group name at runtime
        var resolvedQueueGroupName = ResolveQueueGroupPlaceholders(nca.QueueGroupName);

        // If queue group name is empty, use consumerId; otherwise use resolved queue group name
        var queueGroup = string.IsNullOrEmpty(resolvedQueueGroupName) ? consumerId : resolvedQueueGroupName;

        var subscription = client.SubscribeAsync<byte[]>(
            subject,
            queueGroup: queueGroup,
            cancellationToken: ct);

        logger.LogInformation("Subscribed to {Subject} with queue group: {QueueGroup}", subject, queueGroup ?? "none");

        return subscription;
    }

    /// <summary>
    /// Validates the handler signature and extracts T from NatsMsg&lt;T&gt;.
    /// </summary>
    private Type GetPayloadType(string subject)
    {
        PayloadTypeCache.TryGetValue(subject, out var retval);
        if (retval is not null) return retval;

        handler.TryGetValue(subject, out var _handler);
        if (_handler is null)
        {
            throw new InvalidOperationException($"Can't find handler for subject {subject}");
        }
        var payloadType = NatsConsumerHandlerSignature.GetPayloadType(_handler);

        PayloadTypeCache[subject] = payloadType;
        return payloadType;
    }

    /// <summary>
    /// Gets or caches the Validate() method for a given payload type.
    /// Returns null if the type doesn't have a Validate() method.
    /// </summary>
    private MethodInfo? GetValidateMethod(Type payloadType)
    {
        if (ValidateMethodCache.TryGetValue(payloadType, out var cachedMethod))
        {
            return cachedMethod;
        }

        // Look for a public instance method named "Validate" with no parameters
        var validateMethod = payloadType.GetMethod("Validate", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
        ValidateMethodCache[payloadType] = validateMethod;
        return validateMethod;
    }

    /// <summary>
    /// Gets or caches the Data property for a given message wrapper type.
    /// Returns null if the type doesn't have a Data property.
    /// </summary>
    private PropertyInfo? GetDataProperty(Type msgWrapperType)
    {
        if (DataPropertyCache.TryGetValue(msgWrapperType, out var cachedProperty))
        {
            return cachedProperty;
        }

        var dataProperty = msgWrapperType.GetProperty("Data");
        DataPropertyCache[msgWrapperType] = dataProperty;
        return dataProperty;
    }

    /// <summary>
    /// Validates a message payload if it has a Validate() method.
    /// Throws <see cref="MessageValidationException"/> if validation fails.
    /// Does nothing if no validation method exists.
    /// </summary>
    /// <exception cref="MessageValidationException">Thrown when validation fails.</exception>
    private void ValidateMessageOrThrow(object msgObject, Type payloadType, string subject)
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
            var msgWrapperType = msgObject.GetType();
            var dataProperty = GetDataProperty(msgWrapperType);
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
            // Validation failed - throw exception to be handled by parent
            throw new MessageValidationException($"Message validation failed for subject {subject} with payload type {payloadType.Name}.", ex);
        }
    }
    #endregion
}
