using System.Threading.Channels;

/// <summary>
/// Provides a bounded, thread-safe queue abstraction for scheduling background work items
/// associated with a NATS subscription. Internally uses <see cref="Channel{T}"/> with a
/// configurable maximum capacity to apply back-pressure and prevent unbounded memory growth.
/// </summary>
/// <remarks>
/// Use <see cref="QueueBackgroundWorkItem"/> to enqueue delegates representing message handling work.
/// Consumers call <see cref="DequeueAsync"/> or <see cref="ReadAsync"/> (for simple batching) to pull
/// work from the queue. The queue blocks writers when full ( <see cref="BoundedChannelFullMode.Wait"/> )
/// so upstream producers naturally slow down instead of dropping messages.
/// </remarks>
public class NatsSubscriptionQueue
{
    private readonly Channel<WorkItem> _queue;

    /// <summary>
    /// Creates a new <see cref="NatsSubscriptionQueue"/> with a bounded capacity.
    /// </summary>
    /// <param name="maxChannelCapacity">Maximum number of pending work items allowed in the queue before writers block.</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="maxChannelCapacity"/> is less than or equal to zero.</exception>
    /// <remarks>
    /// The queue is configured for multiple producers and multiple consumers. When the queue is full
    /// additional calls to <see cref="QueueBackgroundWorkItem"/> will asynchronously wait until space is available.
    /// </remarks>
    public NatsSubscriptionQueue(int maxChannelCapacity)
    {
        // Use bounded channel to prevent infinite memory growth
        // Set capacity to 1000 as a reasonable limit
        if (maxChannelCapacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxChannelCapacity), "Capacity must be > 0");
        var options = new BoundedChannelOptions(maxChannelCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = false
        };
        _queue = Channel.CreateBounded<WorkItem>(options);
    }

    /// <summary>
    /// Enqueues a background work item for later execution.
    /// </summary>
    /// <param name="workItem">A delegate that performs the work; it receives a <see cref="CancellationToken"/> to observe cancellation.</param>
    /// <returns>A task that completes when the work item has been written to the underlying channel.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="workItem"/> is null.</exception>
    /// <remarks>
    /// If the queue is at capacity this method asynchronously waits until space becomes available.
    /// The provided delegate is not executed until a consumer dequeues it via <see cref="DequeueAsync"/> or <see cref="ReadAsync"/>.
    /// </remarks>
    public async ValueTask QueueBackgroundWorkItem(WorkItem workItem)
    {
        if (workItem.Fn == null)
            throw new ArgumentNullException(nameof(workItem));

        await _queue.Writer.WriteAsync(workItem);
    }

    /// <summary>
    /// Dequeues the next work item, asynchronously waiting until one becomes available or cancellation is requested.
    /// </summary>
    /// <param name="cancellationToken">Token used to cancel the wait.</param>
    /// <returns>The next queued work item.</returns>
    /// <exception cref="OperationCanceledException">Thrown if <paramref name="cancellationToken"/> is cancelled before an item is available.</exception>
    public async Task<WorkItem> DequeueAsync(CancellationToken cancellationToken)
    {
        var workItem = await _queue.Reader.ReadAsync(cancellationToken);
        return workItem;
    }

    /// <summary>
    /// Reads up to N messages from the channel with a timeout.
    /// The first message will block indefinitely until available.
    /// Subsequent messages will use timeout to avoid waiting too long.
    /// </summary>
    /// <param name="maxMessages">Maximum number of messages to read</param>
    /// <param name="timeout">Timeout duration for subsequent messages</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>List of work items read from the channel</returns>
    public async Task<List<WorkItem>> ReadAsync(int maxMessages, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var workItems = new List<WorkItem>();

        // First, block indefinitely for the first message
        var firstWorkItem = await _queue.Reader.ReadAsync(cancellationToken);
        workItems.Add(firstWorkItem);

        // Now try to read additional messages with timeout (up to maxMessages - 1)
        if (maxMessages < 2)
        {
            return workItems;
        }
        var timeoutCts = new CancellationTokenSource(timeout);
        var combinedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        try
        {
            for (int i = 1; i < maxMessages; i++)
            {
                try
                {
                    var workItem = await _queue.Reader.ReadAsync(combinedCts.Token);
                    workItems.Add(workItem);
                }
                catch (OperationCanceledException) when (timeoutCts.Token.IsCancellationRequested)
                {
                    // Timeout reached for additional messages, break
                    break;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    // Main cancellation requested, rethrow
                    throw;
                }
            }
        }
        finally
        {
            timeoutCts.Dispose();
            combinedCts.Dispose();
        }

        return workItems;
    }
}

/// <summary>
/// A class describing the execution status of a WorkItem
/// </summary>
public class WorkItemExecutionStatus
{
    /// <summary>
    /// Successful execution
    /// </summary>
    public static readonly string SUCCESS = "success";

    /// <summary>
    /// Failed execution
    /// </summary>
    public static readonly string FAIL = "fail";
}


/// <summary>
/// Defines the callback function of the WorkItem
/// </summary>
/// <param name="token">Cancellation token</param>
/// <returns></returns>
public delegate Task<(string status, bool isRetryable)> WorkItemFn(CancellationToken token);



/// <summary>
/// Defines a struct for each item in the queue
/// </summary>
/// <param name="Subject">The subject for which this was scheduled</param>
/// <param name="Fn">The callback fn that processes the incoming message</param>
public readonly record struct WorkItem(string Subject, WorkItemFn Fn);

