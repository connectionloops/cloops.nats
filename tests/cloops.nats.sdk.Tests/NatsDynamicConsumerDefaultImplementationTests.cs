using CLOOPS.NATS;
using CLOOPS.NATS.Locking;
using Microsoft.Extensions.DependencyInjection;
using NATS.Client.Core;
using NATS.Client.JetStream;
using NATS.Client.KeyValueStore;
using Xunit;

namespace cloops.nats.sdk.Tests;

/// <summary>
/// Tests the default interface implementation of the five argument <c>MapConsumers</c>, which exists so a
/// third party <see cref="ICloopsNatsClient"/> compiled before runtime consumers existed keeps compiling.
/// It must never silently drop consumers it cannot register.
/// </summary>
public class NatsDynamicConsumerDefaultImplementationTests
{
    private static readonly string[] NoAssemblies = ["__no_assembly_matches_this__"];

    [Fact]
    public async Task DefaultImplementation_Forwards_WhenThereIsNothingDynamic()
    {
        var client = new ThirdPartyClient();
        using var sp = new ServiceCollection().BuildServiceProvider();

        await ((ICloopsNatsClient)client).MapConsumers(sp, CancellationToken.None, NoAssemblies, true, null);

        Assert.Equal(1, client.FourArgCalls);
    }

    [Fact]
    public async Task DefaultImplementation_Throws_WhenConsumersAreSuppliedExplicitly()
    {
        var client = new ThirdPartyClient();
        using var sp = new ServiceCollection().BuildServiceProvider();

        var ex = await Assert.ThrowsAsync<NotSupportedException>(() =>
            ((ICloopsNatsClient)client).MapConsumers(sp, CancellationToken.None, NoAssemblies, true, [NewConsumer()]));

        Assert.Contains(nameof(ThirdPartyClient), ex.Message);
        Assert.Contains("consumers were supplied", ex.Message);
        Assert.Equal(0, client.FourArgCalls);
    }

    [Fact]
    public async Task DefaultImplementation_Throws_WhenASourceIsRegistered()
    {
        // The recommended usage pattern: no explicit list, just AddNatsDynamicConsumerSource<T>().
        // Inspecting only the argument would forward here and silently ignore the source.
        var client = new ThirdPartyClient();
        var services = new ServiceCollection();
        services.AddNatsDynamicConsumerSource<StubSource>();
        using var sp = services.BuildServiceProvider();

        var ex = await Assert.ThrowsAsync<NotSupportedException>(() =>
            ((ICloopsNatsClient)client).MapConsumers(sp, CancellationToken.None, NoAssemblies, true, null));

        Assert.Contains(nameof(ThirdPartyClient), ex.Message);
        Assert.Contains(nameof(INatsDynamicConsumerSource), ex.Message);
        Assert.Equal(0, client.FourArgCalls);
        Assert.False(StubSource.Called);
    }

    private static NatsDynamicConsumer NewConsumer() =>
        new("cbb.lane.1.>", typeof(Handler), typeof(Handler).GetMethod(nameof(Handler.Handle))!, "cbb-lane-1");

    public class Handler
    {
        public Task<CLOOPS.NATS.Meta.NatsAck> Handle(NatsMsg<string> msg, CancellationToken ct = default)
            => Task.FromResult(new CLOOPS.NATS.Meta.NatsAck(true));
    }

    public class StubSource : INatsDynamicConsumerSource
    {
        public static bool Called;

        public ValueTask<IReadOnlyCollection<NatsDynamicConsumer>> GetConsumersAsync(CancellationToken ct = default)
        {
            Called = true;
            return ValueTask.FromResult<IReadOnlyCollection<NatsDynamicConsumer>>([NewConsumer()]);
        }
    }

    /// <summary>
    /// A third party <see cref="ICloopsNatsClient"/> that implements only the members that existed before
    /// runtime consumer registration, and therefore inherits the default five argument implementation.
    /// </summary>
    private sealed class ThirdPartyClient : ICloopsNatsClient
    {
        public int FourArgCalls { get; private set; }

        public Task MapConsumers(IServiceProvider sp, CancellationToken ct = default, string[]? assemblyNameFilters = null, bool throwOnDuplicate = true)
        {
            FourArgCalls++;
            return Task.CompletedTask;
        }

        // Everything below is irrelevant to these tests.
        public INatsConnection Connection => throw new NotImplementedException();
        public INatsJSContext JsContext => throw new NotImplementedException();
        public INatsJSContext CreateJetStreamContext() => throw new NotImplementedException();
        public INatsJSContext CreateJetStreamContext(NatsJSOpts opts) => throw new NotImplementedException();
        public INatsKVContext CreateKVContext() => throw new NotImplementedException();
        public INatsKVContext CreateKVContext(NatsKVOpts opts) => throw new NotImplementedException();
        public Task SetupKVStoresAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<DistributedLockHandle?> AcquireDistributedLockAsync(string key, TimeSpan? timeout = null, string? ownerId = null, CancellationToken ct = default) => throw new NotImplementedException();
        public ValueTask ConnectAsync() => throw new NotImplementedException();
        public ValueTask<TimeSpan> PingAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public ValueTask PublishAsync<T>(string subject, T data, NatsHeaders? headers = default, string? replyTo = default, INatsSerialize<T>? serializer = default, NatsPubOpts? opts = default, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public ValueTask PublishAsync(string subject, NatsHeaders? headers = default, string? replyTo = default, NatsPubOpts? opts = default, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public IAsyncEnumerable<NatsMsg<T>> SubscribeAsync<T>(string subject, string? queueGroup = default, INatsDeserialize<T>? serializer = default, NatsSubOpts? opts = default, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public ValueTask<NatsMsg<TReply>> RequestAsync<TRequest, TReply>(string subject, TRequest? data, NatsHeaders? headers = default, INatsSerialize<TRequest>? requestSerializer = default, INatsDeserialize<TReply>? replySerializer = default, NatsPubOpts? requestOpts = default, NatsSubOpts? replyOpts = default, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public ValueTask<NatsMsg<TReply>> RequestAsync<TReply>(string subject, INatsDeserialize<TReply>? replySerializer = default, NatsSubOpts? replyOpts = default, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public ValueTask ReconnectAsync() => throw new NotImplementedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
