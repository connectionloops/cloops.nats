using CLOOPS.NATS;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NATS.Client.JetStream;
using Xunit;

namespace cloops.nats.sdk.Tests;

/// <summary>
/// Tests for the processor's environment driven configuration: the work queue capacity
/// (sized relative to <c>NATS_CONSUMER_MAX_DOP</c>) and the JetStream double-ack default.
/// </summary>
public class NatsSubscriptionProcessorConfigTests
{
    #region queue capacity

    [Fact]
    public void QueueCapacity_DefaultsToTwiceMaxDop()
    {
        // A deep queue silently adds delivery-to-ack latency (depth x handler time / MaxDOP); once
        // that exceeds the consumer's AckWait the server redelivers messages still sitting in memory.
        // The default is therefore tied to the worker pool, not a large absolute number.
        using var _ = new EnvVar("NATS_SUBSCRIPTION_QUEUE_SIZE", null);
        using var __ = new EnvVar("NATS_CONSUMER_MAX_DOP", null);

        var processor = NewProcessor();

        Assert.Equal(256, processor.QueueCapacity); // default MaxDOP 128 x 2
    }

    [Fact]
    public void QueueCapacity_FollowsAConfiguredMaxDop()
    {
        using var _ = new EnvVar("NATS_SUBSCRIPTION_QUEUE_SIZE", null);
        using var __ = new EnvVar("NATS_CONSUMER_MAX_DOP", "10");

        Assert.Equal(20, NewProcessor().QueueCapacity);
    }

    [Fact]
    public void QueueCapacity_ExplicitQueueSizeWins()
    {
        using var _ = new EnvVar("NATS_SUBSCRIPTION_QUEUE_SIZE", "5000");
        using var __ = new EnvVar("NATS_CONSUMER_MAX_DOP", "10");

        Assert.Equal(5000, NewProcessor().QueueCapacity);
    }

    #endregion queue capacity

    #region double ack

    [Fact]
    public void DoubleAck_IsOnByDefault()
    {
        using var _ = new EnvVar("NATS_CONSUMER_DOUBLE_ACK", null);

        var processor = NewProcessor();

        Assert.True(processor.DoubleAckByDefault);
        Assert.True(processor.EffectiveAckOpts(null)!.Value.DoubleAck);
    }

    [Fact]
    public void DoubleAck_AppliesWhenTheHandlerLeftTheDecisionOpen()
    {
        using var _ = new EnvVar("NATS_CONSUMER_DOUBLE_ACK", null);

        // Opts supplied, but DoubleAck not set: the default fills it in.
        var effective = NewProcessor().EffectiveAckOpts(new AckOpts());

        Assert.True(effective!.Value.DoubleAck);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DoubleAck_NeverOverridesAnExplicitHandlerChoice(bool handlerChoice)
    {
        using var _ = new EnvVar("NATS_CONSUMER_DOUBLE_ACK", null);

        var effective = NewProcessor().EffectiveAckOpts(new AckOpts { DoubleAck = handlerChoice });

        Assert.Equal(handlerChoice, effective!.Value.DoubleAck);
    }

    [Fact]
    public void DoubleAck_CanBeDisabled()
    {
        using var _ = new EnvVar("NATS_CONSUMER_DOUBLE_ACK", "false");

        var processor = NewProcessor();

        Assert.False(processor.DoubleAckByDefault);
        Assert.Null(processor.EffectiveAckOpts(null));
        Assert.Null(processor.EffectiveAckOpts(new AckOpts()).Value.DoubleAck);
    }

    [Fact]
    public void DoubleAck_FallsBackToEnabled_WhenValueIsNotABoolean()
    {
        using var _ = new EnvVar("NATS_CONSUMER_DOUBLE_ACK", "not-a-bool");

        Assert.True(NewProcessor().DoubleAckByDefault);
    }

    #endregion double ack

    #region helpers

    private static NatsSubscriptionProcessor NewProcessor()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        var sp = services.BuildServiceProvider();

        var client = new CloopsNatsClient(url: "nats://127.0.0.1:14222", name: "cloops.nats.sdk.Tests");
        return new NatsSubscriptionProcessor(sp, client, "config-probe-consumer");
    }

    /// <summary>Sets an environment variable for the duration of a test and restores it afterwards.</summary>
    private sealed class EnvVar : IDisposable
    {
        private readonly string _name;
        private readonly string? _previous;

        public EnvVar(string name, string? value)
        {
            _name = name;
            _previous = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }

        public void Dispose() => Environment.SetEnvironmentVariable(_name, _previous);
    }

    #endregion helpers
}
