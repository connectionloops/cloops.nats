using CLOOPS.NATS.Attributes;
using NATS.Client.Core;
using CLOOPS.NATS.Messages.CP.Infra;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Net;
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
    /// </summary>
    /// <param name="msg"></param>
    /// <param name="ct"></param>
    /// <returns></returns>
    [NatsConsumer("dev.echo", false)]
    public async Task<NatsAck> EchoBack(NatsMsg<string> msg, CancellationToken ct = default)
    {
        _logger.LogInformation("Received message: {Message}", msg.Data);
        // Handle the message
        return new NatsAck(true, msg.Data);
    }

    /// <summary>
    /// Handles EffectTriggered events from CP instances
    /// </summary>
    /// <param name="msg">The NATS message containing the EffectTriggered payload</param>
    /// <param name="ct">Cancellation token for the async operation</param>
    [NatsConsumer("CP.*.EffectTriggered", false)]
    public async Task<NatsAck> HandleEffectTriggered(NatsMsg<EffectTriggered> msg, CancellationToken ct = default)
    {
        if (msg.Data != null)
        {
            Console.WriteLine($"Received effect triggered event with ID: {msg.Data.Id}");
            Console.WriteLine($"URL: {msg.Data.Url}, Method: {msg.Data.Method}");
        }
        // Handle the message
        return new NatsAck(true);
    }

    /// <summary>
    /// Example of using queue groups for load balancing
    /// Multiple instances with the same queue group will share the workload
    /// </summary>
    /// <param name="msg">The NATS message containing the EffectTriggered payload</param>
    /// <param name="ct">Cancellation token for the async operation</param>
    [NatsConsumer("CP.*.EffectTriggered.LoadBalanced", _queueGroupName: "effect-processors")]
    public async Task HandleEffectTriggeredWithQueueGroup(NatsMsg<EffectTriggered> msg, CancellationToken ct = default)
    {
        if (msg.Data != null)
        {
            Console.WriteLine($"[QUEUE GROUP] Worker received effect event with ID: {msg.Data.Id}");
            Console.WriteLine($"[QUEUE GROUP] Processing URL: {msg.Data.Url}, Method: {msg.Data.Method}");

            // Simulate some work
            await Task.Delay(1000, ct).ConfigureAwait(false);
            Console.WriteLine($"[QUEUE GROUP] Finished processing event ID: {msg.Data.Id}");
        }
    }
}

/// <summary>
/// Example showing how to use the consumer registration system
/// </summary>
public class ConsumerHost
{
    /// <summary>
    /// Demonstrates how to register and use NATS consumers
    /// </summary>
    public static async Task NatsConsumerRunner()
    {
        // create a host for DI
        var host = Host.CreateDefaultBuilder()
        // Configure logging providers & level explicitly (in addition to defaults)
        .ConfigureLogging(logging =>
        {
            logging.ClearProviders(); // start clean so we control providers
            logging.AddConsole();     // console output for examples
            logging.SetMinimumLevel(LogLevel.Information);
        })
        .ConfigureServices(services =>
        {
            // register your consumer in DI container
            services.AddSingleton<ConsumerExample>();
            services.AddSingleton<JetStreamDurableConsumer>();
            services.AddSingleton<HybridConsumerExample>();
            services.AddSingleton<QueueGroupTestConsumer>();
            services.AddSingleton<CloopsNatsClient>();
            // register logging abstractions so ILogger<T> can be injected/resolved
            services.AddLogging();

        })
        .Build();

        await host.StartAsync().ConfigureAwait(false);

        // Resolve client + map consumers using the container so ctor deps get injected
        var client = host.Services.GetRequiredService<CloopsNatsClient>();
        var consumeTask = client.MapConsumers(host.Services, throwOnDuplicate: false);

        // sleep so that consumer registration is completed
        Console.WriteLine("Consumer registration completed!");
        Console.WriteLine("The following consumers are now listening:");
        Console.WriteLine("- CP.*.EffectTriggered");
        Console.WriteLine("Listening... Press Ctrl+C to quit.");

        // disable for console interaction
        // await consumeTask;


        // enable this for console interaction
        var sb = new SubjectBuilders.CP.CPSubjectBuilder(client);
        var subject = sb.EventSubjects("cloudpathology_deesha").P_EffectTriggered;
        ConsoleKeyInfo k;
        do
        {
            await subject.Publish(new EffectTriggered
            {
                Id = Guid.NewGuid().ToString(),
                Url = "https://example.com/effect",
                Method = HttpMethod.Post,
                Body = "from pub: human " + DateTime.Now,
                Headers = new CPRequestHeaders { Cpt = "" },
                Response = "response",
                StatusCode = HttpStatusCode.Accepted,
                SysCreated = DateTime.Now,
                // Add other required properties if needed
            }).ConfigureAwait(false);
            Console.WriteLine("Published an event. You should see an output. Press 's' to send another one. Press 'q' to quit");
            k = Console.ReadKey();
        } while (k.KeyChar == 's');
        Console.WriteLine("Bye!");
    }
}
