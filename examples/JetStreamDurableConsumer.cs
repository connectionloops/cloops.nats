using CLOOPS.NATS.Attributes;
using NATS.Client.Core;
using CLOOPS.NATS.Messages.CP.Infra;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CLOOPS.NATS.Examples;

/// <summary>
/// Example consumer class showing how to use JetStream durable consumers
/// Durable consumers provide message persistence and guaranteed delivery
/// </summary>
public class JetStreamDurableConsumer
{
    /// <summary>
    /// Example of a durable consumer that survives application restarts
    /// </summary>
    /// <param name="msg">The NATS message containing the EffectTriggered payload</param>
    /// <param name="ct">Cancellation token for the async operation</param>
    [NatsConsumer("CP.*.EffectTriggered.Durable",
        _durable: true,
        _consumerId: "effect-processor-durable")]
    public async Task HandleDurableEffectTriggered(NatsMsg<EffectTriggered> msg, CancellationToken ct = default)
    {
        if (msg.Data != null)
        {
            Console.WriteLine($"[DURABLE] Received effect triggered event with ID: {msg.Data.Id}");
            Console.WriteLine($"[DURABLE] URL: {msg.Data.Url}, Method: {msg.Data.Method}");

            // Simulate processing
            await Task.Delay(2000, ct).ConfigureAwait(false);
            Console.WriteLine($"[DURABLE] Finished processing event ID: {msg.Data.Id}");
        }
    }

    /// <summary>
    /// Example of a durable consumer with high parallelism for bulk processing
    /// </summary>
    /// <param name="msg">The NATS message containing the EffectTriggered payload</param>
    /// <param name="ct">Cancellation token for the async operation</param>
    [NatsConsumer("CP.*.EffectTriggered.BulkDurable",
        _durable: true,
        _consumerId: "bulk-processor-durable",
        _maxDOP: 50)]
    public async Task HandleBulkDurableEffect(NatsMsg<EffectTriggered> msg, CancellationToken ct = default)
    {
        if (msg.Data != null)
        {
            Console.WriteLine($"[BULK DURABLE] Processing effect ID: {msg.Data.Id} in parallel");
            await Task.Delay(500, ct).ConfigureAwait(false);
            Console.WriteLine($"[BULK DURABLE] Completed effect ID: {msg.Data.Id}");
        }
    }
}

/// <summary>
/// Example of combining durable consumers with regular consumers
/// </summary>
public class HybridConsumerExample
{
    /// <summary>
    /// Fast, non-durable consumer for real-time notifications
    /// </summary>
    [NatsConsumer("notifications.realtime")]
    public async Task HandleRealtimeNotification(NatsMsg<string> msg, CancellationToken ct = default)
    {
        Console.WriteLine($"[REALTIME] Notification: {msg.Data}");
        await Task.CompletedTask;
    }

    /// <summary>
    /// Durable consumer for critical audit events
    /// </summary>
    [NatsConsumer("audit.critical",
        _durable: true,
        _consumerId: "audit-processor")]
    public async Task HandleCriticalAudit(NatsMsg<string> msg, CancellationToken ct = default)
    {
        Console.WriteLine($"[AUDIT DURABLE] Critical event: {msg.Data}");
        // This message is guaranteed to be processed even if app restarts
        await Task.Delay(1000, ct).ConfigureAwait(false);
    }
}
