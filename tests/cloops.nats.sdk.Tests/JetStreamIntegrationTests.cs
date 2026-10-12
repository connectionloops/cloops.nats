using CLOOPS.NATS;
using CLOOPS.NATS.Meta;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NATS.Client.Core;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;
using Xunit;

namespace cloops.nats.sdk.Tests;

/// <summary>
/// Runs only when <c>NATS_TEST_URL</c> points at a JetStream enabled server, for example
/// <c>podman run -d -p 14333:4222 nats:2.11.7-alpine -js</c> with <c>NATS_TEST_URL=nats://127.0.0.1:14333</c>.
/// CI starts such a server; locally the tests are reported as skipped when the variable is unset.
/// </summary>
public sealed class JetStreamFactAttribute : FactAttribute
{
    public const string UrlVariable = "NATS_TEST_URL";

    public JetStreamFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(UrlVariable)))
        {
            Skip = $"Set {UrlVariable} to a JetStream enabled NATS server to run this test.";
        }
    }
}

/// <summary>
/// End-to-end JetStream tests against a real server. They cover the consumer paths that broke when
/// consumers resolved NATS.Client.Core 3.x underneath the NATS.Client.JetStream 2.6.x that cloops.nats used
/// to depend on (ordered consumers threw <see cref="MissingMethodException"/>, filtered consumer creation
/// was rejected), plus the SDK's own durable consumer path, whose message type changed in NATS.Net 3.
/// </summary>
public class JetStreamIntegrationTests : IAsyncLifetime
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    private readonly string stream = $"CLOOPS_IT_{Guid.NewGuid():N}";
    private readonly string prefix = $"cloops-it.{Guid.NewGuid():N}";
    private CloopsNatsClient client = null!;

    public async Task InitializeAsync()
    {
        var url = Environment.GetEnvironmentVariable(JetStreamFactAttribute.UrlVariable);
        if (string.IsNullOrWhiteSpace(url))
        {
            return; // every test in this class is skipped
        }

        client = new CloopsNatsClient(url, "cloops.nats.sdk.Tests.JetStream", creds: null);
        await client.ConnectAsync();
        await client.JsContext.CreateStreamAsync(new StreamConfig(stream, [$"{prefix}.>"]));
    }

    public async Task DisposeAsync()
    {
        if (client is null)
        {
            return;
        }

        try
        {
            await client.JsContext.DeleteStreamAsync(stream);
        }
        catch (NatsJSApiException)
        {
            // already gone
        }

        await client.DisposeAsync();
    }

    [JetStreamFact]
    public async Task OrderedConsumer_ConsumesEveryMessageInStreamOrder()
    {
        await PublishAsync("a", 1, 2, 3, 4, 5);

        using var cts = new CancellationTokenSource(Timeout);
        var consumer = await client.JsContext.CreateOrderedConsumerAsync(stream, cancellationToken: cts.Token);

        var received = new List<(ulong Sequence, int Value)>();
        await foreach (var msg in consumer.ConsumeAsync<ItPayload>(cancellationToken: cts.Token))
        {
            received.Add((msg.Metadata!.Value.Sequence.Stream, msg.Data!.Value));
            if (received.Count == 5)
            {
                break;
            }
        }

        Assert.Equal([1, 2, 3, 4, 5], received.Select(r => r.Value));
        Assert.Equal([1UL, 2UL, 3UL, 4UL, 5UL], received.Select(r => r.Sequence));
    }

    [JetStreamFact]
    public async Task OrderedConsumer_WithFilterSubjects_FetchesOnlyMatchingMessages()
    {
        await PublishAsync("a", 1);
        await PublishAsync("b", 2);
        await PublishAsync("a", 3);
        await PublishAsync("c", 4);

        using var cts = new CancellationTokenSource(Timeout);
        var consumer = await client.JsContext.CreateOrderedConsumerAsync(
            stream,
            new NatsJSOrderedConsumerOpts { FilterSubjects = [$"{prefix}.a", $"{prefix}.c"] },
            cts.Token);

        var received = new List<int>();
        await foreach (var msg in consumer.FetchAsync<ItPayload>(new NatsJSFetchOpts { MaxMsgs = 3, Expires = TimeSpan.FromSeconds(5) }, cancellationToken: cts.Token))
        {
            received.Add(msg.Data!.Value);
        }

        Assert.Equal([1, 3, 4], received);
    }

    [JetStreamFact]
    public async Task FilteredConsumer_SingleFilterSubject_IsCreatedAndDeliversOnlyMatchingMessages()
    {
        await PublishAsync("a", 1);
        await PublishAsync("b", 2);
        await PublishAsync("a", 3);

        using var cts = new CancellationTokenSource(Timeout);
        var consumer = await client.JsContext.CreateOrUpdateConsumerAsync(
            stream,
            new ConsumerConfig("filtered-single") { FilterSubject = $"{prefix}.a", AckPolicy = ConsumerConfigAckPolicy.Explicit },
            cts.Token);

        Assert.Equal($"{prefix}.a", consumer.Info.Config.FilterSubject);
        Assert.Equal(2UL, consumer.Info.NumPending);

        var received = await FetchAndAckAsync(consumer, 2, cts.Token);
        Assert.Equal([1, 3], received);
    }

    [JetStreamFact]
    public async Task FilteredConsumer_NamedWildcardFilterSubject_IsCreatedAndDeliversOnlyMatchingMessages()
    {
        // Named consumer with a wildcard filter, the shape of the incident repro's verify consumer
        // (cp.audit.<instance>.*). The consumer must be named: NATS.Net (2.x and 3.x alike) puts the filter
        // into the create subject without a name token for an unnamed consumer, which the server rejects with
        // "Consumer create request did not match filtered subject from create subject".
        await PublishAsync("audit.one", 1);
        await PublishAsync("other.one", 2);
        await PublishAsync("audit.two", 3);

        using var cts = new CancellationTokenSource(Timeout);
        var consumer = await client.JsContext.CreateOrUpdateConsumerAsync(
            stream,
            new ConsumerConfig
            {
                Name = "filtered-wildcard",
                FilterSubject = $"{prefix}.audit.*",
                AckPolicy = ConsumerConfigAckPolicy.Explicit,
                DeliverPolicy = ConsumerConfigDeliverPolicy.All,
                InactiveThreshold = TimeSpan.FromMinutes(1),
            },
            cts.Token);

        Assert.Equal($"{prefix}.audit.*", consumer.Info.Config.FilterSubject);
        Assert.Equal(2UL, consumer.Info.NumPending);

        var received = await FetchAndAckAsync(consumer, 2, cts.Token);
        Assert.Equal([1, 3], received);
    }

    [JetStreamFact]
    public async Task FilteredConsumer_MultipleFilterSubjects_IsCreatedAndDeliversOnlyMatchingMessages()
    {
        await PublishAsync("a", 1);
        await PublishAsync("b", 2);
        await PublishAsync("c", 3);
        await PublishAsync("b", 4);

        using var cts = new CancellationTokenSource(Timeout);
        var consumer = await client.JsContext.CreateOrUpdateConsumerAsync(
            stream,
            new ConsumerConfig("filtered-multi") { FilterSubjects = [$"{prefix}.a", $"{prefix}.c"], AckPolicy = ConsumerConfigAckPolicy.Explicit },
            cts.Token);

        Assert.Equal([$"{prefix}.a", $"{prefix}.c"], consumer.Info.Config.FilterSubjects!);
        Assert.Equal(2UL, consumer.Info.NumPending);

        var received = await FetchAndAckAsync(consumer, 2, cts.Token);
        Assert.Equal([1, 3], received);
    }

    [JetStreamFact]
    public async Task DurableSdkConsumer_InvokesHandlerAndAcksEachMessage()
    {
        const string consumerId = "sdk-durable";
        await client.JsContext.CreateOrUpdateConsumerAsync(
            stream,
            new ConsumerConfig(consumerId) { FilterSubject = $"{prefix}.sdk.>", AckPolicy = ConsumerConfigAckPolicy.Explicit });

        await PublishAsync("sdk.one", 10);
        await PublishAsync("sdk.two", 20);

        var handler = new DurableHandler(expected: 2);
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton(handler);
        var interceptor = new MetadataCapturingInterceptor();
        services.AddSingleton<INatsConsumerInterceptor>(interceptor);
        using var sp = services.BuildServiceProvider();

        var dynamicConsumer = new NatsDynamicConsumer(
            subject: $"{prefix}.sdk.>",
            handlerType: typeof(DurableHandler),
            handlerMethod: typeof(DurableHandler).GetMethod(nameof(DurableHandler.Handle))!,
            consumerId: consumerId);

        using var stop = new CancellationTokenSource();
        var running = client.MapConsumers(sp, stop.Token, ["__no_assembly_matches_this__"], true, [dynamicConsumer]);

        try
        {
            var completed = await Task.WhenAny(handler.AllReceived, Task.Delay(Timeout));
            Assert.Same(handler.AllReceived, completed);
            Assert.Equal([10, 20], handler.Values.Order());
            Assert.Equal(2, interceptor.Metadata.Count);
            Assert.All(interceptor.Metadata, m =>
            {
                var metadata = Assert.NotNull(m);
                Assert.Equal(stream, metadata.Stream);
                Assert.Equal(consumerId, metadata.Consumer);
                Assert.Equal(1UL, metadata.NumDelivered);
            });

            // Acks are sent after the handler returns; wait for the server to record both.
            var deadline = DateTime.UtcNow + Timeout;
            ConsumerInfo info;
            do
            {
                info = (await client.JsContext.GetConsumerAsync(stream, consumerId)).Info;
                if (info.AckFloor.StreamSeq == 2 && info.NumAckPending == 0)
                {
                    break;
                }

                await Task.Delay(100);
            }
            while (DateTime.UtcNow < deadline);

            Assert.Equal(2UL, info.AckFloor.StreamSeq);
            Assert.Equal(0, info.NumAckPending);
        }
        finally
        {
            stop.Cancel();
            await Task.WhenAny(running, Task.Delay(Timeout));
        }
    }

    private async Task PublishAsync(string token, params int[] values)
    {
        foreach (var value in values)
        {
            var ack = await client.JsContext.PublishAsync($"{prefix}.{token}", new ItPayload { Value = value });
            ack.EnsureSuccess();
        }
    }

    private static async Task<List<int>> FetchAndAckAsync(INatsJSConsumer consumer, int count, CancellationToken ct)
    {
        var received = new List<int>();
        await foreach (var msg in consumer.FetchAsync<ItPayload>(new NatsJSFetchOpts { MaxMsgs = count, Expires = TimeSpan.FromSeconds(5) }, cancellationToken: ct))
        {
            received.Add(msg.Data!.Value);
            await msg.AckAsync(cancellationToken: ct);
        }

        return received;
    }

    public class ItPayload
    {
        public int Value { get; set; }
    }

    public class DurableHandler(int expected)
    {
        private readonly TaskCompletionSource allReceived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly object gate = new();

        public List<int> Values { get; } = [];

        public Task AllReceived => allReceived.Task;

        public Task<NatsAck> Handle(NatsMsg<ItPayload> msg, CancellationToken ct = default)
        {
            lock (gate)
            {
                Values.Add(msg.Data!.Value);
                if (Values.Count == expected)
                {
                    allReceived.TrySetResult();
                }
            }

            return Task.FromResult(new NatsAck(true));
        }
    }

    public class MetadataCapturingInterceptor : INatsConsumerInterceptor
    {
        private readonly object gate = new();

        public List<NatsConsumerJetStreamMetadata?> Metadata { get; } = [];

        public ValueTask<NatsConsumerInterceptorResult> InterceptAsync(NatsConsumerInterceptorContext context, CancellationToken ct = default)
        {
            lock (gate)
            {
                Metadata.Add(context.JetStream);
            }

            return ValueTask.FromResult(NatsConsumerInterceptorResult.Continue());
        }
    }
}
