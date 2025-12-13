namespace CLOOPS.NATS.Attributes;

/// <summary>
/// Attribute to mark a method as a NATS message consumer
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public class NatsConsumerAttribute : Attribute
{
    /// <summary>
    /// The subject you want to listen to
    /// Wildcards (*) and (>) are supported
    /// Read https://docs.nats.io/nats-concepts/subjects#wildcards for more details
    /// </summary>
    public string Subject { get; }

    /// <summary>
    /// Durable Consumer ID for JetStream consumers
    /// Provides consumer persistence across restarts.
    /// Specifying this automatically makes the consumer durable
    /// Make sure consumer id exists
    /// </summary>
    public string ConsumerId { get; }

    /// <summary>
    /// NATS Queue Group Name
    /// Only valid for core subscriptions
    /// Durable subscriptions control it through consumer spec
    /// </summary>
    public string QueueGroupName { get; init; } = "";

    /// <summary>
    /// Specifies if the consumer is durable or not
    /// </summary>
    public bool IsDurable { get; init; }

    /// <summary>
    /// Initializes a new instance of NatsConsumerAttribute
    /// </summary>
    /// <param name="_subject">The subject to listen to. This value is assigned to the <see cref="Subject"/> property.</param>
    /// <param name="_consumerId">Durable Consumer ID for JetStream. Required when _durable is true.</param>
    /// <param name="_QueueGroupName">Nats queue group name. only valid for core subscriptions</param>
    public NatsConsumerAttribute(
        string _subject,
        string? _consumerId = null,
        string _QueueGroupName = ""
    )
    {
        Subject = _subject;
        if (_consumerId is null)
        {
            IsDurable = false;
            QueueGroupName = _QueueGroupName;
            // if non durable, then construct a unique consumer id per subject
            ConsumerId = $"{_subject}-${QueueGroupName}";

        }
        else
        {
            IsDurable = true;
            ConsumerId = _consumerId!;
        }
    }
}
