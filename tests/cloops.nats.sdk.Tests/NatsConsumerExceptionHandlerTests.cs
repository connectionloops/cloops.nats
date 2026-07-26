using CLOOPS.NATS;
using CLOOPS.NATS.Meta;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NATS.Client.Core;
using Xunit;

namespace cloops.nats.sdk.Tests;

/// <summary>
/// Tests for consumer exception-handler registration and invocation pipeline.
/// </summary>
public class NatsConsumerExceptionHandlerTests
{
    [Fact]
    public void AddNatsConsumerExceptionHandler_RegistersSingletonInDi()
    {
        var services = new ServiceCollection();
        services.AddNatsConsumerExceptionHandler<MappingExceptionHandler>();

        using var sp = services.BuildServiceProvider();
        var handlers = sp.GetServices<INatsConsumerExceptionHandler>().ToArray();

        Assert.Single(handlers);
        Assert.IsType<MappingExceptionHandler>(handlers[0]);
    }

    [Fact]
    public async Task InvokeAsync_ReturnsHandlerAck_WhenHandlerSucceeds()
    {
        var ack = await InvokeAsync(nameof(SampleConsumer.Succeed), "ok");

        Assert.True(ack.IsAcknowledged);
        Assert.Equal("ok", ack.Reply);
        Assert.False(ack.IsExceptionMapped);
        Assert.Null(ack.MappedException);
        Assert.Null(ack.MappedByHandlerType);
    }

    [Fact]
    public async Task InvokeAsync_Rethrows_WhenHandlerThrowsAndNoExceptionHandlerRegistered()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            InvokeAsync(nameof(SampleConsumer.Fail), "boom"));

        Assert.Equal("handler failed", ex.Message);
    }

    [Fact]
    public async Task InvokeAsync_PreservesOriginalStackTrace_WhenRethrowing()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            InvokeAsync(nameof(SampleConsumer.FailDeep), "boom"));

        // The throw site must survive the trip through the invoker's catch block.
        Assert.Contains(nameof(SampleConsumer.ThrowDeep), ex.StackTrace);
    }

    [Fact]
    public async Task InvokeAsync_UsesExceptionHandlerAck_WhenHandlerThrows()
    {
        var ack = await InvokeAsync(nameof(SampleConsumer.Fail), "boom", new MappingExceptionHandler());

        Assert.True(ack.IsAcknowledged);
        Assert.Equal("mapped:handler failed", ack.Reply);
        Assert.False(ack.ShouldRetryDelivery);
    }

    [Fact]
    public async Task InvokeAsync_MarksAckAsExceptionMapped()
    {
        var ack = await InvokeAsync(nameof(SampleConsumer.Fail), "boom", new MappingExceptionHandler());

        Assert.True(ack.IsExceptionMapped);
        Assert.IsType<InvalidOperationException>(ack.MappedException);
        Assert.Equal("handler failed", ack.MappedException!.Message);
        Assert.Equal(typeof(MappingExceptionHandler), ack.MappedByHandlerType);
    }

    [Fact]
    public async Task InvokeAsync_DoesNotMutateAckReturnedByExceptionHandler()
    {
        // Handlers are free to return shared instances; marking must not leak across invocations.
        var shared = new NatsAck(true, "shared", _shouldRetryDelivery: false);
        var handler = new SharedAckExceptionHandler(shared);

        var ack = await InvokeAsync(nameof(SampleConsumer.Fail), "boom", handler);

        Assert.True(ack.IsExceptionMapped);
        Assert.False(shared.IsExceptionMapped);
        Assert.NotSame(shared, ack);
        Assert.Equal(shared.Reply, ack.Reply);
        Assert.Equal(shared.IsAcknowledged, ack.IsAcknowledged);
        Assert.Equal(shared.ShouldRetryDelivery, ack.ShouldRetryDelivery);
    }

    [Fact]
    public async Task InvokeAsync_UsesFirstNonNullExceptionHandlerAck()
    {
        var ack = await InvokeAsync(
            nameof(SampleConsumer.Fail),
            "boom",
            new DecliningExceptionHandler(),
            new MappingExceptionHandler(),
            new TerminalExceptionHandler());

        Assert.Equal("mapped:handler failed", ack.Reply);
    }

    [Fact]
    public async Task InvokeAsync_Rethrows_WhenAllExceptionHandlersDecline()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            InvokeAsync(nameof(SampleConsumer.Fail), "boom", new DecliningExceptionHandler()));
    }

    [Fact]
    public async Task InvokeAsync_SkipsThrowingExceptionHandler_AndUsesNextOne()
    {
        var ack = await InvokeAsync(
            nameof(SampleConsumer.Fail),
            "boom",
            new ThrowingExceptionHandler(),
            new MappingExceptionHandler());

        Assert.Equal("mapped:handler failed", ack.Reply);
        Assert.Equal("handler failed", ack.MappedException!.Message);
    }

    [Fact]
    public async Task InvokeAsync_RethrowsOriginalException_WhenOnlyExceptionHandlerThrows()
    {
        // The original fault must survive; a broken exception handler must not replace it.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            InvokeAsync(nameof(SampleConsumer.Fail), "boom", new ThrowingExceptionHandler()));

        Assert.Equal("handler failed", ex.Message);
    }

    [Fact]
    public async Task InvokeAsync_LogsWarning_WhenExceptionIsMapped()
    {
        var logger = new CapturingLogger();

        await InvokeAsync(nameof(SampleConsumer.Fail), "boom", logger, new MappingExceptionHandler());

        var warning = Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains(nameof(SampleConsumer.Fail), warning.Message);
        Assert.Contains(nameof(MappingExceptionHandler), warning.Message);
        Assert.Equal("handler failed", warning.Exception?.Message);
    }

    [Fact]
    public async Task InvokeAsync_LogsError_WhenExceptionHandlerThrows()
    {
        var logger = new CapturingLogger();

        await InvokeAsync(
            nameof(SampleConsumer.Fail),
            "boom",
            logger,
            new ThrowingExceptionHandler(),
            new MappingExceptionHandler());

        var error = Assert.Single(logger.Entries, e => e.Level == LogLevel.Error);
        Assert.Contains(nameof(ThrowingExceptionHandler), error.Message);
        Assert.Equal("exception handler is broken", error.Exception?.Message);
    }

    [Fact]
    public async Task InvokeAsync_PropagatesCancellation_WithoutCallingExceptionHandler()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var probe = new ProbeExceptionHandler();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            NatsConsumerHandlerInvoker.InvokeAsync(
                CreatePlan(nameof(SampleConsumer.Cancel)),
                CreateMsg("cancel"),
                rawData: null,
                jetStream: null,
                exceptionHandlers: new INatsConsumerExceptionHandler[] { probe },
                logger: NullLogger.Instance,
                ct: cts.Token));

        Assert.Equal(0, probe.CallCount);
    }

    [Fact]
    public void ExceptionContext_ExposesMessageAndExceptionMetadata()
    {
        var method = typeof(SampleConsumer).GetMethod(nameof(SampleConsumer.Fail))!;
        var msg = CreateMsg("payload");
        var exception = new InvalidOperationException("x");

        var context = new NatsConsumerExceptionContext(
            "configured.subject",
            typeof(string),
            typeof(SampleConsumer),
            method,
            msg,
            exception,
            rawData: [1, 2, 3]);

        Assert.Equal("configured.subject", context.MatchedSubject);
        Assert.Equal(typeof(string), context.PayloadType);
        Assert.Equal(typeof(SampleConsumer), context.HandlerType);
        Assert.Same(method, context.HandlerMethod);
        Assert.Same(exception, context.Exception);
        Assert.Equal(new byte[] { 1, 2, 3 }, context.RawData);
        Assert.Equal("payload", context.GetMessage<string>().Data);
    }

    [Fact]
    public void ExceptionContext_ReadsTransportMetadataFromMessage()
    {
        var context = new NatsConsumerExceptionContext(
            "configured.subject",
            typeof(string),
            typeof(SampleConsumer),
            typeof(SampleConsumer).GetMethod(nameof(SampleConsumer.Fail))!,
            CreateMsg("payload"),
            new InvalidOperationException("x"),
            rawData: null);

        // Repeated reads exercise the cached metadata path.
        Assert.Equal("test.subject", context.Subject);
        Assert.Equal("test.subject", context.Subject);
        Assert.Equal("reply.subject", context.ReplyTo);
        Assert.Null(context.Headers);
    }

    [Fact]
    public void ExceptionContext_Constructor_RejectsMessageThatDoesNotMatchPayloadType()
    {
        var ex = Assert.Throws<ArgumentException>(() => new NatsConsumerExceptionContext(
            "configured.subject",
            typeof(int),
            typeof(SampleConsumer),
            typeof(SampleConsumer).GetMethod(nameof(SampleConsumer.Fail))!,
            CreateMsg("payload"),
            new InvalidOperationException("x"),
            rawData: null));

        Assert.Equal("message", ex.ParamName);
        Assert.Contains("NatsMsg<Int32>", ex.Message);
    }

    [Fact]
    public void ExceptionContext_Constructor_RejectsNullArguments()
    {
        Assert.Throws<ArgumentNullException>(() => new NatsConsumerExceptionContext(
            "configured.subject",
            typeof(string),
            typeof(SampleConsumer),
            typeof(SampleConsumer).GetMethod(nameof(SampleConsumer.Fail))!,
            CreateMsg("payload"),
            exception: null!,
            rawData: null));
    }

    private static Task<NatsAck> InvokeAsync(
        string methodName,
        string data,
        params INatsConsumerExceptionHandler[] exceptionHandlers)
        => InvokeAsync(methodName, data, NullLogger.Instance, exceptionHandlers);

    [Fact]
    public async Task InvokeAsync_PassesJetStreamMetadataToExceptionHandler()
    {
        var jetStream = new NatsConsumerJetStreamMetadata(
            NumDelivered: 4, NumPending: 0, StreamSequence: 900, ConsumerSequence: 12,
            Timestamp: DateTimeOffset.UnixEpoch, Stream: "ORDERS", Consumer: "orders-durable", Domain: null);
        var probe = new MetadataCapturingExceptionHandler();

        await NatsConsumerHandlerInvoker.InvokeAsync(
            CreatePlan(nameof(SampleConsumer.Fail)),
            CreateMsg("boom"),
            rawData: null,
            jetStream: jetStream,
            exceptionHandlers: new INatsConsumerExceptionHandler[] { probe },
            logger: NullLogger.Instance,
            ct: CancellationToken.None);

        Assert.True(probe.WasJetStream);
        Assert.Equal(4ul, probe.NumDelivered);
    }

    [Fact]
    public async Task InvokeAsync_ReportsCoreTransport_WhenNoJetStreamMetadata()
    {
        var probe = new MetadataCapturingExceptionHandler();

        await InvokeAsync(nameof(SampleConsumer.Fail), "boom", probe);

        Assert.False(probe.WasJetStream);
    }

    private static Task<NatsAck> InvokeAsync(
        string methodName,
        string data,
        ILogger logger,
        params INatsConsumerExceptionHandler[] exceptionHandlers)
        => NatsConsumerHandlerInvoker.InvokeAsync(
            CreatePlan(methodName),
            CreateMsg(data),
            rawData: null,
            jetStream: null,
            exceptionHandlers: exceptionHandlers,
            logger: logger,
            ct: CancellationToken.None);

    private static NatsConsumerInvocationPlan CreatePlan(string methodName)
        => new(
            "test.subject",
            typeof(string),
            typeof(SampleConsumer),
            typeof(SampleConsumer).GetMethod(methodName)!,
            new SampleConsumer());

    private static NatsMsg<string> CreateMsg(string data)
        => new(
            subject: "test.subject",
            replyTo: "reply.subject",
            size: data.Length,
            headers: null,
            data: data,
            connection: null!,
            flags: default);

    private sealed class SampleConsumer
    {
        public Task<NatsAck> Succeed(NatsMsg<string> msg, CancellationToken ct)
            => Task.FromResult(new NatsAck(true, msg.Data, _shouldRetryDelivery: false));

        public Task<NatsAck> Fail(NatsMsg<string> msg, CancellationToken ct)
            => throw new InvalidOperationException("handler failed");

        public Task<NatsAck> FailDeep(NatsMsg<string> msg, CancellationToken ct)
        {
            ThrowDeep();
            return Task.FromResult(new NatsAck(true));
        }

        public Task<NatsAck> Cancel(NatsMsg<string> msg, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new NatsAck(true));
        }

        internal static void ThrowDeep() => throw new InvalidOperationException("deep failure");
    }

    private sealed class MappingExceptionHandler : INatsConsumerExceptionHandler
    {
        public ValueTask<NatsAck?> HandleExceptionAsync(NatsConsumerExceptionContext context, CancellationToken ct = default)
            => ValueTask.FromResult<NatsAck?>(
                new NatsAck(true, $"mapped:{context.Exception.Message}", _shouldRetryDelivery: false));
    }

    private sealed class DecliningExceptionHandler : INatsConsumerExceptionHandler
    {
        public ValueTask<NatsAck?> HandleExceptionAsync(NatsConsumerExceptionContext context, CancellationToken ct = default)
            => ValueTask.FromResult<NatsAck?>(null);
    }

    private sealed class TerminalExceptionHandler : INatsConsumerExceptionHandler
    {
        public ValueTask<NatsAck?> HandleExceptionAsync(NatsConsumerExceptionContext context, CancellationToken ct = default)
            => ValueTask.FromResult<NatsAck?>(new NatsAck(false, "should-not-win"));
    }

    private sealed class ThrowingExceptionHandler : INatsConsumerExceptionHandler
    {
        public ValueTask<NatsAck?> HandleExceptionAsync(NatsConsumerExceptionContext context, CancellationToken ct = default)
            => throw new NotSupportedException("exception handler is broken");
    }

    private sealed class SharedAckExceptionHandler(NatsAck ack) : INatsConsumerExceptionHandler
    {
        public ValueTask<NatsAck?> HandleExceptionAsync(NatsConsumerExceptionContext context, CancellationToken ct = default)
            => ValueTask.FromResult<NatsAck?>(ack);
    }

    private sealed class MetadataCapturingExceptionHandler : INatsConsumerExceptionHandler
    {
        public bool WasJetStream { get; private set; }

        public ulong NumDelivered { get; private set; }

        public ValueTask<NatsAck?> HandleExceptionAsync(NatsConsumerExceptionContext context, CancellationToken ct = default)
        {
            WasJetStream = context.IsJetStream;
            NumDelivered = context.JetStream?.NumDelivered ?? 0;
            return ValueTask.FromResult<NatsAck?>(new NatsAck(true));
        }
    }

    private sealed class ProbeExceptionHandler : INatsConsumerExceptionHandler
    {
        public int CallCount { get; private set; }

        public ValueTask<NatsAck?> HandleExceptionAsync(NatsConsumerExceptionContext context, CancellationToken ct = default)
        {
            CallCount++;
            return ValueTask.FromResult<NatsAck?>(new NatsAck(true));
        }
    }

    private sealed class CapturingLogger : ILogger
    {
        public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception), exception));
    }
}
