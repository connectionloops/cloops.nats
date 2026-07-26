using System.Reflection;
using CLOOPS.NATS;
using CLOOPS.NATS.Meta;
using NATS.Client.Core;
using Xunit;

namespace cloops.nats.sdk.Tests;

/// <summary>
/// Exercises the public context constructors the way a consuming application would when unit
/// testing its own interceptors and exception handlers, without any NATS infrastructure.
/// </summary>
public class NatsConsumerContextConstructionTests
{
    [Fact]
    public async Task ApplicationCanUnitTestItsOwnExceptionHandler()
    {
        var context = new NatsConsumerExceptionContext(
            matchedSubject: "orders.created",
            payloadType: typeof(Order),
            handlerType: typeof(OrderController),
            handlerMethod: HandlerMethod,
            message: CreateMsg(),
            exception: new ArgumentException("bad order"),
            rawData: null);

        var ack = await new AppExceptionHandler().HandleExceptionAsync(context);

        Assert.NotNull(ack);
        Assert.True(ack!.IsAcknowledged);
        Assert.Equal("BAD_REQUEST: bad order", ack.Reply);
        Assert.Equal("orders.created", context.Subject);
    }

    [Fact]
    public async Task ApplicationCanUnitTestItsOwnInterceptor()
    {
        var context = new NatsConsumerInterceptorContext(
            matchedSubject: "orders.created",
            payloadType: typeof(Order),
            handlerType: typeof(OrderController),
            handlerMethod: HandlerMethod,
            message: CreateMsg(),
            rawData: null);

        var result = await new NormalizingInterceptor().InterceptAsync(context);

        Assert.True(result.ShouldContinue);
        Assert.Equal("WIDGET", context.GetMessage<Order>().Data!.Name);
    }

    [Fact]
    public void InterceptorContext_RereadsMetadataAfterMessageReplacement()
    {
        var context = new NatsConsumerInterceptorContext(
            "orders.created", typeof(Order), typeof(OrderController), HandlerMethod, CreateMsg(), null);

        Assert.Equal("reply.inbox", context.ReplyTo);

        context.ReplaceMessage(new NatsMsg<Order>(
            subject: "orders.rerouted",
            replyTo: null,
            size: 0,
            headers: null,
            data: new Order { Name = "x" },
            connection: null!,
            flags: default));

        // The cached metadata from the previous message must not survive the swap.
        Assert.Equal("orders.rerouted", context.Subject);
        Assert.Null(context.ReplyTo);
    }

    [Fact]
    public void Context_DefaultsToCoreNats_WhenNoJetStreamMetadataSupplied()
    {
        var context = new NatsConsumerExceptionContext(
            "orders.created", typeof(Order), typeof(OrderController), HandlerMethod,
            CreateMsg(), new InvalidOperationException("x"), rawData: null);

        Assert.False(context.IsJetStream);
        Assert.Null(context.JetStream);
    }

    [Fact]
    public void Context_ExposesJetStreamDeliveryInformation()
    {
        var jetStream = new NatsConsumerJetStreamMetadata(
            NumDelivered: 3,
            NumPending: 7,
            StreamSequence: 1200,
            ConsumerSequence: 44,
            Timestamp: new DateTimeOffset(2026, 7, 25, 12, 0, 0, TimeSpan.Zero),
            Stream: "ORDERS",
            Consumer: "orders-durable",
            Domain: null);

        var context = new NatsConsumerExceptionContext(
            "orders.created", typeof(Order), typeof(OrderController), HandlerMethod,
            CreateMsg(), new InvalidOperationException("x"), rawData: null, jetStream: jetStream);

        Assert.True(context.IsJetStream);
        Assert.Equal(3ul, context.JetStream!.Value.NumDelivered);
        Assert.Equal(1200ul, context.JetStream.Value.StreamSequence);
        Assert.Equal("ORDERS", context.JetStream.Value.Stream);
        Assert.True(context.JetStream.Value.IsRedelivery);
    }

    [Fact]
    public void JetStreamMetadata_FirstDeliveryIsNotARedelivery()
    {
        var first = new NatsConsumerJetStreamMetadata(1, 0, 1, 1, DateTimeOffset.UnixEpoch, "S", "C", null);

        Assert.False(first.IsRedelivery);
    }

    [Fact]
    public void InterceptorContext_Constructor_RejectsMessageThatDoesNotMatchPayloadType()
    {
        var ex = Assert.Throws<ArgumentException>(() => new NatsConsumerInterceptorContext(
            "orders.created", typeof(string), typeof(OrderController), HandlerMethod, CreateMsg(), null));

        Assert.Equal("message", ex.ParamName);
    }

    private static MethodInfo HandlerMethod
        => typeof(OrderController).GetMethod(nameof(OrderController.Handle))!;

    private static NatsMsg<Order> CreateMsg()
        => new(
            subject: "orders.created",
            replyTo: "reply.inbox",
            size: 12,
            headers: null,
            data: new Order { Id = 1, Name = "widget" },
            connection: null!,
            flags: default);

    private sealed class Order
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
    }

    private sealed class OrderController
    {
        public Task<NatsAck> Handle(NatsMsg<Order> msg, CancellationToken ct)
            => Task.FromResult(new NatsAck(true));
    }

    private sealed class AppExceptionHandler : INatsConsumerExceptionHandler
    {
        public ValueTask<NatsAck?> HandleExceptionAsync(NatsConsumerExceptionContext context, CancellationToken ct = default)
            => context.Exception switch
            {
                ArgumentException arg => ValueTask.FromResult<NatsAck?>(
                    new NatsAck(true, $"BAD_REQUEST: {arg.Message}", _shouldRetryDelivery: false)),
                _ => ValueTask.FromResult<NatsAck?>(null)
            };
    }

    private sealed class NormalizingInterceptor : INatsConsumerInterceptor
    {
        public ValueTask<NatsConsumerInterceptorResult> InterceptAsync(NatsConsumerInterceptorContext context, CancellationToken ct = default)
        {
            var order = context.GetMessage<Order>().Data!;
            context.ReplaceData(new Order { Id = order.Id, Name = order.Name.ToUpperInvariant() });
            return ValueTask.FromResult(NatsConsumerInterceptorResult.Continue());
        }
    }
}
