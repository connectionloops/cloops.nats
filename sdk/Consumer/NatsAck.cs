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
    /// The original handler exception when this ack was produced by an
    /// <c>INatsConsumerExceptionHandler</c> instead of being returned by the consumer handler.
    /// <c>null</c> for acks returned normally by a handler.
    /// </summary>
    public Exception? MappedException { get; }

    /// <summary>
    /// The exception handler type that produced this ack from <see cref="MappedException"/>.
    /// <c>null</c> for acks returned normally by a handler.
    /// </summary>
    public Type? MappedByHandlerType { get; }

    /// <summary>
    /// Indicates that this ack was synthesized from a handler exception rather than returned by
    /// the handler. The platform still applies the ack/nak/terminate and reply semantics it
    /// carries, but records the invocation as a failure for metrics purposes.
    /// </summary>
    public bool IsExceptionMapped => MappedException is not null;

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

    /// <summary>
    /// Copies an exception handler's ack and records the exception it was mapped from.
    /// </summary>
    /// <param name="source">The ack returned by the exception handler.</param>
    /// <param name="mappedException">The original handler exception.</param>
    /// <param name="mappedByHandlerType">The exception handler type that produced <paramref name="source"/>.</param>
    /// <remarks>
    /// A copy is taken rather than mutating <paramref name="source"/> because handlers are free to
    /// return shared or cached <see cref="NatsAck"/> instances.
    /// </remarks>
    internal NatsAck(NatsAck source, Exception mappedException, Type mappedByHandlerType)
        : this(source.IsAcknowledged, source.Reply, source.Opts, source.ShouldRetryDelivery)
    {
        MappedException = mappedException;
        MappedByHandlerType = mappedByHandlerType;
    }
}

