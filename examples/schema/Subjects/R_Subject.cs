using NATS.Client.Core;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;

namespace CLOOPS.NATS;

/// <summary>
/// Represents a subject you can send a request to and expect an reply back
/// Please note: Request-Reply is only applicable in core nats. 
/// </summary>
/// <typeparam name="Q">Type of request(Question)</typeparam>
/// <typeparam name="A">Type of reply(Answer)</typeparam>
public class R_Subject<Q, A>
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
    public R_Subject(ICloopsNatsClient cnc, string SubjectName)
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
    /// </summary>
    /// <param name="payload">Payload to send</param>
    /// <returns>void</returns>
    public ValueTask<NatsMsg<A>> Request(Q payload)
    {
        return cnc.RequestAsync<Q, A>(SubjectName, payload);
    }
}