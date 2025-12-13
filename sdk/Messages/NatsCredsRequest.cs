namespace CLOOPS.NATS.Messages;

/// <summary>
/// Request message to mint a NATS user credentials
/// </summary>
public class NatsCredsRequest
{
    /// <summary>
    /// The name of the user
    /// </summary>
    public string userName { get; set; } = "no-user";

    /// <summary>
    /// The list of subjects the user is allowed to publish to
    /// </summary>
    public List<String> allowPubs { get; set; } = [];

    /// <summary>
    /// The list of subjects the user is denied to publish to
    /// </summary>
    public List<String> denyPubs { get; set; } = [];

    /// <summary>
    /// The list of subjects the user is allowed to subscribe to
    /// </summary>
    public List<String> allowSubs { get; set; } = [];

    /// <summary>
    /// The list of subjects the user is denied to subscribe to
    /// </summary>
    public List<String> denySubs { get; set; } = [];

    /// <summary>
    /// The expiration time of the credentials in milliseconds
    /// </summary>
    public long expMs { get; set; } = 300_000; // 5 mins 

}