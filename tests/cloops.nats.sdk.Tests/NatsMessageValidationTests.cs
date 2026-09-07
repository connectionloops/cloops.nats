using System.ComponentModel.DataAnnotations;
using System.Reflection;
using CLOOPS.NATS;
using CLOOPS.NATS.Meta;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NATS.Client.Core;
using Xunit;

namespace cloops.nats.sdk.Tests;

/// <summary>
/// Tests for payload validation and how a validation failure reaches the exception-handler pipeline.
/// </summary>
/// <remarks>
/// Payload validation used to run on the subscription loop, before the message was ever dispatched,
/// and a failure was logged and dropped. On core request/reply that left the caller with nothing but
/// a timeout, because no <see cref="INatsConsumerExceptionHandler"/> could see the message. These
/// tests pin the current contract: the failure is offered to the exception handlers, and if none of
/// them claims it the message is discarded exactly as before.
/// </remarks>
public class NatsMessageValidationTests
{
    private static readonly NatsConsumerJetStreamMetadata JetStream = new(
        NumDelivered: 1, NumPending: 0, StreamSequence: 12, ConsumerSequence: 3,
        Timestamp: DateTimeOffset.UnixEpoch, Stream: "CBB", Consumer: "cbb-durable", Domain: null);

    [Fact]
    public async Task InvokeWithValidationAsync_InvokesHandler_WhenPayloadIsValid()
    {
        var consumer = new SampleConsumer();

        var ack = await InvokeAsync(consumer, new ReviewPayload { Comments = "looks good" });

        Assert.Equal(1, consumer.Invocations);
        Assert.NotNull(ack);
        Assert.True(ack!.IsAcknowledged);
        Assert.Equal("handled", ack.Reply);
        Assert.False(ack.IsExceptionMapped);
    }

    [Fact]
    public async Task InvokeWithValidationAsync_InvokesHandler_WhenPayloadHasNoValidateMethod()
    {
        var consumer = new UnvalidatedConsumer();
        var plan = CreatePlan(consumer, nameof(UnvalidatedConsumer.Handle), typeof(string));

        var ack = await NatsConsumerHandlerInvoker.InvokeWithValidationAsync(
            plan, CreateMsg("anything"), rawData: null, jetStream: null,
            exceptionHandlers: [], logger: NullLogger.Instance, ct: CancellationToken.None);

        Assert.Equal(1, consumer.Invocations);
        Assert.True(ack!.IsAcknowledged);
    }

    [Fact]
    public async Task InvokeWithValidationAsync_DoesNotInvokeHandler_WhenValidationFails()
    {
        var consumer = new SampleConsumer();

        await InvokeAsync(consumer, new ReviewPayload { Comments = null }, [new MappingExceptionHandler()]);

        Assert.Equal(0, consumer.Invocations);
    }

    [Fact]
    public async Task InvokeWithValidationAsync_ReturnsExceptionHandlerReply_WhenValidationFailsOnCore()
    {
        // The defect this pins: a core request/reply caller used to get a timeout, because the
        // registered exception handler never saw the message.
        var ack = await InvokeAsync(
            new SampleConsumer(), new ReviewPayload { Comments = null }, [new MappingExceptionHandler()]);

        Assert.NotNull(ack);
        Assert.True(ack!.IsAcknowledged);
        Assert.Equal("ValidationFailed: Comments are required when Decision is Rejected", ack.Reply);
        Assert.True(ack.IsExceptionMapped);
        Assert.Equal(typeof(MappingExceptionHandler), ack.MappedByHandlerType);
    }

    [Fact]
    public async Task InvokeWithValidationAsync_GivesExceptionHandlerTheExceptionTheSchemaThrew()
    {
        // Handlers classify their own schema's validation exception. Handing them a platform wrapper
        // instead would silently reclassify every validation failure in every existing service.
        var probe = new CapturingExceptionHandler();

        await InvokeAsync(new SampleConsumer(), new ReviewPayload { Comments = null }, [probe]);

        Assert.IsType<ValidationException>(probe.Exception);
        Assert.Equal("Comments are required when Decision is Rejected", probe.Exception!.Message);
        Assert.True(probe.IsValidationFailure);
        Assert.False(probe.IsJetStream);
        Assert.Equal("reply.subject", probe.ReplyTo);
    }

    [Fact]
    public async Task InvokeWithValidationAsync_ReportsHandlerFaultsAsNotValidationFailures()
    {
        var probe = new CapturingExceptionHandler();

        await InvokeAsync(new ThrowingConsumer(), new ReviewPayload { Comments = "fine" }, [probe]);

        Assert.False(probe.IsValidationFailure);
        Assert.IsType<InvalidOperationException>(probe.Exception);
    }

    [Fact]
    public async Task InvokeWithValidationAsync_ReturnsNull_WhenValidationFailsAndNoHandlerIsRegistered()
    {
        // The no-handler service must be no worse off than before: the message is dropped, and the
        // caller of a core request still times out. Null tells the processor to discard.
        var consumer = new SampleConsumer();
        var logger = new CapturingLogger();

        var ack = await InvokeAsync(consumer, new ReviewPayload { Comments = null }, logger: logger);

        Assert.Null(ack);
        Assert.Equal(0, consumer.Invocations);

        var error = Assert.Single(logger.Entries, e => e.Level == LogLevel.Error);
        Assert.Contains("Discarding message", error.Message);
        Assert.Contains("test.subject", error.Message);
    }

    [Fact]
    public async Task InvokeWithValidationAsync_ReturnsNull_WhenEveryExceptionHandlerDeclines()
    {
        var ack = await InvokeAsync(
            new SampleConsumer(), new ReviewPayload { Comments = null }, [new DecliningExceptionHandler()]);

        Assert.Null(ack);
    }

    [Fact]
    public async Task InvokeWithValidationAsync_KeepsExceptionHandlerTerminateOnJetStream()
    {
        var ack = await InvokeAsync(
            new SampleConsumer(), new ReviewPayload { Comments = null }, [new TerminatingExceptionHandler()], jetStream: JetStream);

        Assert.NotNull(ack);
        Assert.False(ack!.IsAcknowledged);
        Assert.False(ack.ShouldRetryDelivery);
    }

    [Fact]
    public async Task InvokeWithValidationAsync_TerminatesInsteadOfNaking_WhenHandlerAsksForRedeliveryOnJetStream()
    {
        // Redelivery cannot make an invalid payload valid, so a nak here is a loop until MaxDeliver.
        var logger = new CapturingLogger();

        var ack = await InvokeAsync(
            new SampleConsumer(), new ReviewPayload { Comments = null }, [new NakingExceptionHandler()], logger, JetStream);

        Assert.NotNull(ack);
        Assert.False(ack!.IsAcknowledged);
        Assert.False(ack.ShouldRetryDelivery);
        Assert.True(ack.IsExceptionMapped);
        Assert.Equal(typeof(NakingExceptionHandler), ack.MappedByHandlerType);

        var warning = Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("terminated instead"));
        Assert.Contains(nameof(NakingExceptionHandler), warning.Message);
    }

    [Fact]
    public async Task InvokeWithValidationAsync_HonoursNakForHandlerFaultsOnJetStream()
    {
        // The nak downgrade is specific to validation; a genuine handler fault still gets redelivered.
        var ack = await InvokeAsync(
            new ThrowingConsumer(), new ReviewPayload { Comments = "fine" }, [new NakingExceptionHandler()],
            jetStream: JetStream);

        Assert.NotNull(ack);
        Assert.False(ack!.IsAcknowledged);
        Assert.True(ack.ShouldRetryDelivery);
    }

    [Fact]
    public async Task InvokeWithValidationAsync_UsesFirstExceptionHandlerThatClaimsTheValidationFailure()
    {
        var ack = await InvokeAsync(
            new SampleConsumer(),
            new ReviewPayload { Comments = null },
            [new DecliningExceptionHandler(), new MappingExceptionHandler(), new TerminatingExceptionHandler()]);

        Assert.Equal("ValidationFailed: Comments are required when Decision is Rejected", ack!.Reply);
    }

    [Fact]
    public async Task InvokeWithValidationAsync_SkipsValidation_WhenPayloadIsNull()
    {
        var consumer = new SampleConsumer();
        var plan = CreatePlan(consumer, nameof(SampleConsumer.Handle), typeof(ReviewPayload));

        var ack = await NatsConsumerHandlerInvoker.InvokeWithValidationAsync(
            plan, CreateMsg<ReviewPayload>(null!), rawData: null, jetStream: null,
            exceptionHandlers: [], logger: NullLogger.Instance, ct: CancellationToken.None);

        Assert.Equal(1, consumer.Invocations);
        Assert.True(ack!.IsAcknowledged);
    }

    [Fact]
    public void ExceptionContext_IsValidationFailure_DefaultsToFalseOnTheOriginalConstructor()
    {
        // The shipped 8 argument constructor keeps its exact signature; the flag is a new overload.
        var context = new NatsConsumerExceptionContext(
            "configured.subject",
            typeof(ReviewPayload),
            typeof(SampleConsumer),
            typeof(SampleConsumer).GetMethod(nameof(SampleConsumer.Handle))!,
            CreateMsg(new ReviewPayload()),
            new InvalidOperationException("x"),
            rawData: null);

        Assert.False(context.IsValidationFailure);
    }

    [Fact]
    public void ExceptionContext_IsValidationFailure_IsSetByTheNewConstructor()
    {
        var context = new NatsConsumerExceptionContext(
            "configured.subject",
            typeof(ReviewPayload),
            typeof(SampleConsumer),
            typeof(SampleConsumer).GetMethod(nameof(SampleConsumer.Handle))!,
            CreateMsg(new ReviewPayload()),
            new ValidationException("bad"),
            rawData: null,
            jetStream: null,
            isValidationFailure: true);

        Assert.True(context.IsValidationFailure);
    }

    /// <summary>
    /// Runs the validate-then-invoke pipeline against a consumer whose handler method is <c>Handle</c>.
    /// </summary>
    private static Task<NatsAck?> InvokeAsync(
        object consumer,
        ReviewPayload? payload,
        INatsConsumerExceptionHandler[]? exceptionHandlers = null,
        ILogger? logger = null,
        NatsConsumerJetStreamMetadata? jetStream = null)
        => NatsConsumerHandlerInvoker.InvokeWithValidationAsync(
            CreatePlan(consumer, "Handle", typeof(ReviewPayload)),
            CreateMsg(payload!),
            rawData: null,
            jetStream: jetStream,
            exceptionHandlers: exceptionHandlers ?? [],
            logger: logger ?? NullLogger.Instance,
            ct: CancellationToken.None);

    private static NatsConsumerInvocationPlan CreatePlan(object consumer, string methodName, Type payloadType)
        => new(
            "test.subject",
            payloadType,
            consumer.GetType(),
            consumer.GetType().GetMethod(methodName)!,
            consumer);

    private static NatsMsg<T> CreateMsg<T>(T data)
        => new(
            subject: "test.subject",
            replyTo: "reply.subject",
            size: 0,
            headers: null,
            data: data,
            connection: null!,
            flags: default);

    /// <summary>
    /// Stands in for a schema message: a public parameterless <c>Validate()</c> that throws the
    /// schema's own exception type, exactly like <c>BaseMessage.Validate()</c> does.
    /// </summary>
    private sealed class ReviewPayload
    {
        public string? Comments { get; init; }

        public void Validate()
        {
            if (string.IsNullOrWhiteSpace(Comments))
                throw new ValidationException("Comments are required when Decision is Rejected");
        }
    }

    private sealed class SampleConsumer
    {
        public int Invocations { get; private set; }

        public Task<NatsAck> Handle(NatsMsg<ReviewPayload> msg, CancellationToken ct)
        {
            Invocations++;
            return Task.FromResult(new NatsAck(true, "handled", _shouldRetryDelivery: false));
        }
    }

    private sealed class ThrowingConsumer
    {
        public Task<NatsAck> Handle(NatsMsg<ReviewPayload> msg, CancellationToken ct)
            => throw new InvalidOperationException("handler failed");
    }

    private sealed class UnvalidatedConsumer
    {
        public int Invocations { get; private set; }

        public Task<NatsAck> Handle(NatsMsg<string> msg, CancellationToken ct)
        {
            Invocations++;
            return Task.FromResult(new NatsAck(true));
        }
    }

    /// <summary>Maps like a service would: a typed reply on core, ack, no redelivery.</summary>
    private sealed class MappingExceptionHandler : INatsConsumerExceptionHandler
    {
        public ValueTask<NatsAck?> HandleExceptionAsync(NatsConsumerExceptionContext context, CancellationToken ct = default)
            => ValueTask.FromResult<NatsAck?>(context.Exception is ValidationException
                ? new NatsAck(true, $"ValidationFailed: {context.Exception.Message}", _shouldRetryDelivery: false)
                : null);
    }

    private sealed class DecliningExceptionHandler : INatsConsumerExceptionHandler
    {
        public ValueTask<NatsAck?> HandleExceptionAsync(NatsConsumerExceptionContext context, CancellationToken ct = default)
            => ValueTask.FromResult<NatsAck?>(null);
    }

    private sealed class TerminatingExceptionHandler : INatsConsumerExceptionHandler
    {
        public ValueTask<NatsAck?> HandleExceptionAsync(NatsConsumerExceptionContext context, CancellationToken ct = default)
            => ValueTask.FromResult<NatsAck?>(new NatsAck(_isAck: false, _shouldRetryDelivery: false));
    }

    private sealed class NakingExceptionHandler : INatsConsumerExceptionHandler
    {
        public ValueTask<NatsAck?> HandleExceptionAsync(NatsConsumerExceptionContext context, CancellationToken ct = default)
            => ValueTask.FromResult<NatsAck?>(new NatsAck(_isAck: false, _shouldRetryDelivery: true));
    }

    private sealed class CapturingExceptionHandler : INatsConsumerExceptionHandler
    {
        public Exception? Exception { get; private set; }

        public bool IsValidationFailure { get; private set; }

        public bool IsJetStream { get; private set; }

        public string? ReplyTo { get; private set; }

        public ValueTask<NatsAck?> HandleExceptionAsync(NatsConsumerExceptionContext context, CancellationToken ct = default)
        {
            Exception = context.Exception;
            IsValidationFailure = context.IsValidationFailure;
            IsJetStream = context.IsJetStream;
            ReplyTo = context.ReplyTo;
            return ValueTask.FromResult<NatsAck?>(new NatsAck(true, _shouldRetryDelivery: false));
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
