using CLOOPS.NATS.Attributes;
using NATS.Client.Core;
using CLOOPS.NATS.Messages.CP.Infra;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Net;

namespace CLOOPS.NATS.Examples;

/// <summary>
/// Comprehensive test class for queue group functionality
/// Tests all possible queue group scenarios
/// </summary>
public class QueueGroupTestConsumer
{
    /// <summary>
    /// Case 1: No queue group specified (null) - all instances receive messages
    /// </summary>
    [NatsConsumer("test.noqueue")]
    public async Task HandleNoQueue(NatsMsg<string> msg, CancellationToken ct = default)
    {
        Console.WriteLine($"[NO QUEUE] Instance received: {msg.Data}");
        await Task.CompletedTask;
    }

    /// <summary>
    /// Case 2: Empty string queue group - should behave like no queue group
    /// </summary>
    [NatsConsumer("test.emptyqueue", _queueGroupName: "")]
    public async Task HandleEmptyQueue(NatsMsg<string> msg, CancellationToken ct = default)
    {
        Console.WriteLine($"[EMPTY QUEUE] Instance received: {msg.Data}");
        await Task.CompletedTask;
    }

    /// <summary>
    /// Case 3: Queue group "workers" - load balanced
    /// </summary>
    [NatsConsumer("test.queuegroup", _queueGroupName: "workers")]
    public async Task HandleQueueGroup1(NatsMsg<string> msg, CancellationToken ct = default)
    {
        Console.WriteLine($"[QUEUE GROUP 'workers'] Instance received: {msg.Data}");
        await Task.Delay(100, ct).ConfigureAwait(false); // Simulate work
    }

    /// <summary>
    /// Case 4: Different queue group "processors" - separate load balancing pool
    /// </summary>
    [NatsConsumer("test.queuegroup", _queueGroupName: "processors")]
    public async Task HandleQueueGroup2(NatsMsg<string> msg, CancellationToken ct = default)
    {
        Console.WriteLine($"[QUEUE GROUP 'processors'] Instance received: {msg.Data}");
        await Task.Delay(100, ct).ConfigureAwait(false); // Simulate work
    }

    /// <summary>
    /// Case 5: Same queue group as Case 3 - should load balance with HandleQueueGroup1
    /// </summary>
    [NatsConsumer("test.queuegroup", _queueGroupName: "workers")]
    public async Task HandleQueueGroup3(NatsMsg<string> msg, CancellationToken ct = default)
    {
        Console.WriteLine($"[QUEUE GROUP 'workers' - METHOD 2] Instance received: {msg.Data}");
        await Task.Delay(100, ct).ConfigureAwait(false); // Simulate work
    }
}
