using CLOOPS.NATS.Attributes;
using NATS.Client.Core;
using Microsoft.Extensions.Logging;
using CLOOPS.NATS.Meta;

namespace CLOOPS.NATS.Examples;

/// <summary>
/// Example consumer class showing how to use NATS consumer attributes
/// This demonstrates real domain events from the CLOOPS system
/// </summary>
public class ConsumerExample
{
    private readonly ILogger<ConsumerExample> _logger;

    public ConsumerExample(ILogger<ConsumerExample> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// echo back the message
    /// This is responder i.e. it sends reply back
    /// </summary>
    /// <param name="msg"></param>
    /// <param name="ct"></param>
    /// <returns></returns>
    [NatsConsumer("dev.echo")]
    public Task<NatsAck> EchoBack(NatsMsg<string> msg, CancellationToken ct = default)
    {
        _logger.LogInformation("Received message: {Message}", msg.Data);
        // Handle the message - return with reply data for request-reply pattern
        return Task.FromResult(new NatsAck(true, msg.Data));
    }

    /// <summary>
    /// Handles save person events
    /// </summary>
    /// <param name="msg">The NATS message containing the Person payload</param>
    /// <param name="ct">Cancellation token for the async operation</param>
    [NatsConsumer("test.persons.*.save")]
    public Task<NatsAck> HandleSavePerson(NatsMsg<Person> msg, CancellationToken ct = default)
    {
        if (msg.Data != null)
        {
            _logger.LogInformation($"Received request to save person with ID: {msg.Data.Id}");
            _logger.LogInformation($"Name: {msg.Data.Name}, Age: {msg.Data.Age}, Addr: {msg.Data.Addr}");
        }
        // ack the message
        return Task.FromResult(new NatsAck(true));
    }


    /// <summary>
    /// Handles update person events
    /// This is a durable consumer
    /// i.e. It will not miss any events if the app restarts thanks to JetStream
    /// IMPORTANT: Make sure stream and consumer are already created
    /// <code lang="bash">
    /// nats stream add TEST_PERSONS_UPDATE --subjects "test.persons.*.update" --max-age 2h
    /// nats consumer add TEST_PERSONS_UPDATE person-durable-consumer
    /// Streams and consumers are typically handled outside code at infra level
    /// </code
    /// </summary>
    /// <param name="msg">The NATS message containing the Person payload</param>
    /// <param name="ct">Cancellation token for the async operation</param>
    [NatsConsumer("test.persons.*.update", _consumerId: "person-durable-consumer")]
    public Task<NatsAck> HandleUpdatePerson(NatsMsg<Person> msg, CancellationToken ct = default)
    {
        if (msg.Data != null)
        {
            _logger.LogInformation($"Received request to update person with ID: {msg.Data.Id}");
            _logger.LogInformation($"Name: {msg.Data.Name}, Age: {msg.Data.Age}, Addr: {msg.Data.Addr}");
        }
        // ack the message
        return Task.FromResult(new NatsAck(true));
    }
}