using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;
using CLOOPS.NATS.Messages;

namespace CLOOPS.NATS;

/// <summary>
/// Represents a Subject you can stream publish (durable publish) an event to
/// This is JetStream publishing
/// Ideally should be only be generated from a `SubjectBuilder`
/// <typeparam name="T">Type of publish payload</typeparam>
/// </summary>
public class S_Subject<T> where T : BaseMessage
{
    /// <summary>
    /// string value of the subject
    /// </summary>
    public string SubjectName { get; }

    private ICloopsNatsClient cnc;

    /// <summary>
    /// The subject constructor
    /// </summary>
    /// <param name="cnc">Cloops Nats Client</param>
    /// <param name="SubjectName">The string value of the subject</param>
    /// <exception cref="ArgumentException"></exception>
    public S_Subject(ICloopsNatsClient cnc, string SubjectName)
    {
        if (string.IsNullOrWhiteSpace(SubjectName))
        {
            throw new ArgumentException("Subject value cannot be null or whitespace.", nameof(SubjectName));
        }
        if (SubjectName.Contains(" "))
        {
            throw new ArgumentException("Subject value cannot contain spaces.", nameof(SubjectName));
        }
        if (SubjectName.Length > 127)
        {
            throw new ArgumentException("Subject value cannot be longer than 255 characters.", nameof(SubjectName));
        }
        if (SubjectName.Split('.').Length > 8)
        {
            throw new ArgumentException("Subject value cannot contain more than 8 tokens.", nameof(SubjectName));
        }
        this.SubjectName = SubjectName;
        this.cnc = cnc;
    }

    /// <summary>
    /// Publishes the message on JetStream Context
    /// Used for durable publishing. Enables you to track the message delivery and ensure success.
    /// Requires the subject to have a stream associated with it
    /// Validates the message before publishing
    /// Throws on failure
    /// </summary>
    /// <param name="payload"></param>
    /// <param name="ensureSuccess">If we need guranteed success</param>
    /// <param name="dedupeId">
    ///     If set, messages will be deduped by this ID. 
    /// </param>
    /// <param name="throwOnDuplicate">
    ///     If true, method throws on duplicate.
    ///     <paramref name="dedupeId"/> needs to be set for this to take effect
    /// </param>
    /// <exception cref="ArgumentNullException"></exception>
    /// <exception cref="System.ComponentModel.DataAnnotations.ValidationException">Thrown when message validation fails.</exception>
    /// <exception cref="NatsJSApiException"></exception>
    /// <exception cref="NatsJSDuplicateMessageException"></exception>
    /// <returns></returns>
    public async Task StreamPublish(T payload, bool ensureSuccess = true, string? dedupeId = null, bool throwOnDuplicate = true)
    {
        payload.Validate();
        ValueTask<PubAckResponse> ack;
        if (dedupeId != null)
        {
            ack = cnc.JsContext.PublishAsync(SubjectName, payload, opts: new() { MsgId = dedupeId });
        }
        else
        {
            ack = cnc.JsContext.PublishAsync(SubjectName, payload);
        }
        if (ensureSuccess)
        {
            try
            {
                (await ack).EnsureSuccess();
            }
            catch (NatsJSDuplicateMessageException)
            {
                if (throwOnDuplicate) throw;
            }
        }
    }
}