using NATS.Client.JetStream;

namespace CLOOPS.NATS;

/// <summary>
/// Read-only JetStream delivery information for the message being processed.
/// </summary>
/// <remarks>
/// This is observability only. Retry budgets and dead-letter behaviour are owned by the stream and
/// consumer configuration in the control plane; nothing here lets an application change them.
/// Note that <c>MaxDeliver</c> is not carried on the message, so "is this the final attempt" cannot
/// be derived from <see cref="NumDelivered"/> alone. Use JetStream's
/// <c>$JS.EVENT.ADVISORY.CONSUMER.MAX_DELIVERIES</c> advisory for terminal escalation instead of
/// hard-coding the limit in application code.
/// </remarks>
/// <param name="NumDelivered">How many times this message has been delivered, starting at 1.</param>
/// <param name="NumPending">Messages still pending for the consumer.</param>
/// <param name="StreamSequence">The message's sequence number within the stream.</param>
/// <param name="ConsumerSequence">The message's sequence number for this consumer.</param>
/// <param name="Timestamp">When the message was stored in the stream.</param>
/// <param name="Stream">The stream the message was read from.</param>
/// <param name="Consumer">The durable consumer that delivered the message.</param>
/// <param name="Domain">The JetStream domain, when one is configured.</param>
public readonly record struct NatsConsumerJetStreamMetadata(
    ulong NumDelivered,
    ulong NumPending,
    ulong StreamSequence,
    ulong ConsumerSequence,
    DateTimeOffset Timestamp,
    string Stream,
    string Consumer,
    string? Domain)
{
    /// <summary>
    /// True when this is not the first delivery attempt for the message.
    /// </summary>
    public bool IsRedelivery => NumDelivered > 1;

    /// <summary>
    /// Projects the NATS client metadata onto the SDK's read-only view.
    /// </summary>
    /// <param name="msg">The JetStream message being processed.</param>
    /// <returns>The delivery information, or <c>null</c> when the message carries no metadata.</returns>
    internal static NatsConsumerJetStreamMetadata? From(NatsJSMsg<byte[]> msg)
    {
        if (msg.Metadata is not { } metadata)
            return null;

        return new NatsConsumerJetStreamMetadata(
            metadata.NumDelivered,
            metadata.NumPending,
            metadata.Sequence.Stream,
            metadata.Sequence.Consumer,
            metadata.Timestamp,
            metadata.Stream,
            metadata.Consumer,
            metadata.Domain);
    }
}
