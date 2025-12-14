using CLOOPS.NATS.Messages;

namespace CLOOPS.NATS;

/// <summary>
/// Represents a Subject you can publish an event to
/// This is core NATS publishing
/// Ideally should be only be generated from a `SubjectBuilder`
/// <typeparam name="T">Type of publish payload</typeparam>
/// </summary>
public class P_Subject<T> where T : BaseMessage
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
    /// <param name="SubjectName">The string value of the subjet</param>
    /// <exception cref="ArgumentException"></exception>
    public P_Subject(ICloopsNatsClient cnc, string SubjectName)
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
    /// Publishes an event on the subject using Core NATS
    /// Validates the message before publishing
    /// </summary>
    /// <param name="payload">Payload to send</param>
    /// <returns>void</returns>
    /// <exception cref="System.ComponentModel.DataAnnotations.ValidationException">Thrown when message validation fails.</exception>
    public ValueTask Publish(T payload)
    {
        payload.Validate();
        return cnc.PublishAsync(SubjectName, payload);
    }
}