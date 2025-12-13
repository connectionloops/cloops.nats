using NATS.Client.JetStream;

namespace CLOOPS.NATS.Meta;
/// <summary>
/// A class representing acknowledgement for a message
/// </summary>
public class NatsAck
{
    /// <summary>
    /// Indicates that message is successfully processed
    /// </summary>
    public bool IsAcknowledged { get; }

    /// <summary>
    /// Indicates that the message should be retried by nats server
    /// only valid when isAck is false
    /// </summary>
    public bool ShouldRetryDelivery { get; }

    /// <summary>
    /// Ack Options such as delayed delivery etc.
    /// </summary>
    public AckOpts? Opts { get; }

    /// <summary>
    /// Payload of the message
    /// </summary>
    public object? Reply { get; }

    /// <summary>
    /// Create a new instance of NatsAck
    /// </summary>
    /// <param name="_isAck">Is message successfully ack'd</param>
    /// <param name="_reply">The reply payload to send back to the requester. Should only be used in request-reply paradigm</param>
    /// <param name="_opts">Ack Options. e.g. delayed re-delivery</param>
    /// <param name="_shouldRetryDelivery">Indicates that the message should be retried by nats server. Only valid when isAck is false</param>
    public NatsAck(bool _isAck, object? _reply = default, AckOpts? _opts = null, bool _shouldRetryDelivery = true)
    {

        IsAcknowledged = _isAck;
        Opts = _opts;
        Reply = _reply;
        ShouldRetryDelivery = _shouldRetryDelivery;
    }
}

