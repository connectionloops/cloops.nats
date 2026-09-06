using System.Reflection;
using System.Reflection.Emit;
using CLOOPS.NATS;
using CLOOPS.NATS.Attributes;
using CLOOPS.NATS.Meta;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NATS.Client.Core;
using Xunit;

namespace cloops.nats.sdk.Tests;

/// <summary>
/// Tests for consumers registered at runtime (<see cref="NatsDynamicConsumer"/>) and for the
/// consumer-id to subscription-processor mapping performed by <c>MapConsumers</c>.
/// </summary>
public class NatsDynamicConsumerTests
{
    // A filter that matches no loaded assembly, so only runtime supplied consumers are registered.
    private static readonly string[] NoAssemblies = ["__no_assembly_matches_this__"];

    #region validation

    [Fact]
    public void Ctor_BindsHandler_WhenSignatureIsValid()
    {
        var consumer = NewConsumer("cbb.lane.7.>", "cbb-lane-7");

        Assert.Equal("cbb.lane.7.>", consumer.Subject);
        Assert.Equal("cbb-lane-7", consumer.ConsumerId);
        Assert.True(consumer.IsDurable);
        Assert.Equal(typeof(LaneConsumer), consumer.HandlerType);
        Assert.Equal(nameof(LaneConsumer.Handle), consumer.HandlerMethod.Name);
        Assert.Equal(typeof(LanePayload), consumer.PayloadType);
    }

    [Fact]
    public void Ctor_MarksConsumerAsCore_WhenConsumerIdIsOmitted()
    {
        var consumer = new NatsDynamicConsumer(
            subject: "cbb.probe",
            handlerType: typeof(LaneConsumer),
            handlerMethod: MethodOf(nameof(LaneConsumer.Handle)),
            queueGroupName: "probes");

        Assert.Null(consumer.ConsumerId);
        Assert.False(consumer.IsDurable);
        Assert.Equal("probes", consumer.QueueGroupName);
    }

    [Theory]
    [InlineData(nameof(BadConsumer.WrongParameterCount), "must have exactly 2 parameters")]
    [InlineData(nameof(BadConsumer.WrongFirstParameter), "must be of type NatsMsg<T>")]
    [InlineData(nameof(BadConsumer.WrongReturnType), "must return Task<NatsAck>")]
    public void Ctor_Throws_WhenHandlerSignatureIsInvalid(string methodName, string expectedMessage)
    {
        var method = typeof(BadConsumer).GetMethod(methodName)!;

        var ex = Assert.Throws<InvalidOperationException>(() =>
            new NatsDynamicConsumer("cbb.lane.1", typeof(BadConsumer), method, "cbb-lane-1"));

        Assert.Contains(expectedMessage, ex.Message);
    }

    [Fact]
    public void Ctor_Throws_WhenMethodDoesNotBelongToHandlerType()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            new NatsDynamicConsumer("cbb.lane.1", typeof(BadConsumer), MethodOf(nameof(LaneConsumer.Handle)), "cbb-lane-1"));

        Assert.Contains("not assignable from handler type", ex.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Ctor_Throws_WhenSubjectIsBlank(string subject)
    {
        Assert.Throws<ArgumentException>(() =>
            new NatsDynamicConsumer(subject, typeof(LaneConsumer), MethodOf(nameof(LaneConsumer.Handle)), "cbb-lane-1"));
    }

    [Fact]
    public void Ctor_Throws_WhenConsumerIdIsBlank()
    {
        Assert.Throws<ArgumentException>(() =>
            new NatsDynamicConsumer("cbb.lane.1", typeof(LaneConsumer), MethodOf(nameof(LaneConsumer.Handle)), "  "));
    }

    #endregion validation

    #region di

    [Fact]
    public void AddNatsDynamicConsumerSource_RegistersSingletonInDi()
    {
        var services = new ServiceCollection();
        services.AddNatsDynamicConsumerSource<LaneSource>();

        using var sp = services.BuildServiceProvider();
        var sources = sp.GetServices<INatsDynamicConsumerSource>().ToArray();

        Assert.Single(sources);
        Assert.IsType<LaneSource>(sources[0]);
        Assert.Same(sources[0], sp.GetServices<INatsDynamicConsumerSource>().Single());
    }

    #endregion di

    #region registration

    [Fact]
    public async Task BuildSubscriptionProcessors_BindsHandlerToDurableConsumer_WhenSuppliedAsArgument()
    {
        await using var client = NewClient();
        using var sp = NewServiceProvider();

        var processors = await client.BuildSubscriptionProcessors(
            sp,
            NoAssemblies,
            throwOnDuplicate: true,
            dynamicConsumers: [NewConsumer("cbb.lane.7.>", "cbb-lane-7")],
            CancellationToken.None);

        var processor = Assert.Single(processors).Value;
        Assert.Equal("cbb-lane-7", processor.RegisteredConsumerId);
        Assert.Equal(["cbb.lane.7.>"], processor.RegisteredSubjects);
        Assert.Equal(MethodOf(nameof(LaneConsumer.Handle)), processor.RegisteredHandlers["cbb.lane.7.>"]);
    }

    [Fact]
    public async Task BuildSubscriptionProcessors_BindsOneHandlerToManyDurableConsumers()
    {
        // The lane-split case: one handler, N durable consumers discovered in the control plane.
        await using var client = NewClient();
        using var sp = NewServiceProvider();

        var lanes = Enumerable.Range(1, 4)
            .Select(i => NewConsumer($"cbb.lane.{i}.>", $"cbb-lane-{i}"))
            .ToArray();

        var processors = await client.BuildSubscriptionProcessors(
            sp, NoAssemblies, throwOnDuplicate: true, dynamicConsumers: lanes, CancellationToken.None);

        Assert.Equal(4, processors.Count);
        Assert.Equal(["cbb-lane-1", "cbb-lane-2", "cbb-lane-3", "cbb-lane-4"], processors.Keys.Order());
        Assert.All(processors.Values, p => Assert.Equal(MethodOf(nameof(LaneConsumer.Handle)), p.RegisteredHandlers.Values.Single()));
    }

    [Fact]
    public async Task BuildSubscriptionProcessors_MultiplexesSubjectsOntoOneConsumerId()
    {
        await using var client = NewClient();
        using var sp = NewServiceProvider();

        var processors = await client.BuildSubscriptionProcessors(
            sp,
            NoAssemblies,
            throwOnDuplicate: true,
            dynamicConsumers:
            [
                NewConsumer("cbb.lane.7.a", "cbb-lane-7"),
                NewConsumer("cbb.lane.7.b", "cbb-lane-7")
            ],
            CancellationToken.None);

        var processor = Assert.Single(processors).Value;
        Assert.Equal(["cbb.lane.7.a", "cbb.lane.7.b"], processor.RegisteredSubjects);
    }

    [Fact]
    public async Task BuildSubscriptionProcessors_QueriesRegisteredSources()
    {
        await using var client = NewClient();
        using var sp = NewServiceProvider(services => services.AddNatsDynamicConsumerSource<LaneSource>());

        var processors = await client.BuildSubscriptionProcessors(
            sp, NoAssemblies, throwOnDuplicate: true, dynamicConsumers: null, CancellationToken.None);

        Assert.Equal(2, processors.Count);
        Assert.True(sp.GetServices<INatsDynamicConsumerSource>().OfType<LaneSource>().Single().WasCalled);
        Assert.Equal(["cbb-source-lane-1", "cbb-source-lane-2"], processors.Keys.Order());
    }

    [Fact]
    public async Task BuildSubscriptionProcessors_Throws_WhenDynamicSubjectDuplicatesAnother()
    {
        await using var client = NewClient();
        using var sp = NewServiceProvider();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.BuildSubscriptionProcessors(
                sp,
                NoAssemblies,
                throwOnDuplicate: true,
                dynamicConsumers:
                [
                    NewConsumer("cbb.lane.dupe", "cbb-lane-1"),
                    NewConsumer("cbb.lane.dupe", "cbb-lane-2")
                ],
                CancellationToken.None));

        Assert.Contains("Duplicate consumer found for subject cbb.lane.dupe", ex.Message);
    }

    [Fact]
    public async Task BuildSubscriptionProcessors_IgnoresDuplicateSubject_WhenThrowOnDuplicateIsFalse()
    {
        await using var client = NewClient();
        using var sp = NewServiceProvider();

        var processors = await client.BuildSubscriptionProcessors(
            sp,
            NoAssemblies,
            throwOnDuplicate: false,
            dynamicConsumers:
            [
                NewConsumer("cbb.lane.dupe", "cbb-lane-1"),
                NewConsumer("cbb.lane.dupe", "cbb-lane-2")
            ],
            CancellationToken.None);

        Assert.Equal(["cbb-lane-1"], processors.Keys);
    }

    [Fact]
    public async Task BuildSubscriptionProcessors_RegistersNothing_WhenNoDynamicConsumersSupplied()
    {
        // The default path must stay exactly as it was: no sources registered, no argument passed.
        await using var client = NewClient();
        using var sp = NewServiceProvider();

        var processors = await client.BuildSubscriptionProcessors(
            sp, NoAssemblies, throwOnDuplicate: true, dynamicConsumers: null, CancellationToken.None);

        Assert.Empty(processors);
    }

    #endregion registration

    #region assembly scan

    [Fact]
    public async Task BuildSubscriptionProcessors_CreatesOneProcessorPerConsumerId_AcrossAssemblies()
    {
        // Regression: the processor map used to be created inside the per-assembly loop, so one
        // consumer id declared in two assemblies produced TWO processors, both attaching to the
        // same JetStream consumer.
        const string prefix = "CloopsNatsHoistProbe";
        EmittedConsumerAssembly.Ensure($"{prefix}.A", "probe.hoist.a", "probe-shared-consumer");
        EmittedConsumerAssembly.Ensure($"{prefix}.B", "probe.hoist.b", "probe-shared-consumer");

        await using var client = NewClient();
        using var sp = NewServiceProvider(services =>
        {
            foreach (var type in EmittedConsumerAssembly.TypesFor(prefix))
            {
                services.AddSingleton(type);
            }
        });

        var processors = await client.BuildSubscriptionProcessors(
            sp, [prefix], throwOnDuplicate: true, dynamicConsumers: null, CancellationToken.None);

        var processor = Assert.Single(processors).Value;
        Assert.Equal("probe-shared-consumer", processor.RegisteredConsumerId);
        Assert.Equal(["probe.hoist.a", "probe.hoist.b"], processor.RegisteredSubjects.Order());
    }

    [Fact]
    public async Task BuildSubscriptionProcessors_MergesDynamicConsumerIntoAttributeDeclaredConsumerId()
    {
        const string prefix = "CloopsNatsMergeProbe";
        EmittedConsumerAssembly.Ensure($"{prefix}.A", "probe.merge.a", "probe-merge-consumer");

        await using var client = NewClient();
        using var sp = NewServiceProvider(services =>
        {
            foreach (var type in EmittedConsumerAssembly.TypesFor(prefix))
            {
                services.AddSingleton(type);
            }
        });

        var processors = await client.BuildSubscriptionProcessors(
            sp,
            [prefix],
            throwOnDuplicate: true,
            dynamicConsumers: [NewConsumer("probe.merge.b", "probe-merge-consumer")],
            CancellationToken.None);

        var processor = Assert.Single(processors).Value;
        Assert.Equal(["probe.merge.a", "probe.merge.b"], processor.RegisteredSubjects.Order());
    }

    #endregion assembly scan

    #region helpers

    private static MethodInfo MethodOf(string name) => typeof(LaneConsumer).GetMethod(name)!;

    private static NatsDynamicConsumer NewConsumer(string subject, string consumerId) =>
        new(subject, typeof(LaneConsumer), MethodOf(nameof(LaneConsumer.Handle)), consumerId);

    private static CloopsNatsClient NewClient() => new(url: "nats://127.0.0.1:14222", name: "cloops.nats.sdk.Tests");

    private static ServiceProvider NewServiceProvider(Action<ServiceCollection>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton<LaneConsumer>();
        configure?.Invoke(services);
        return services.BuildServiceProvider();
    }

    /// <summary>Payload used by the test handlers.</summary>
    public class LanePayload
    {
        public string? Id { get; set; }
    }

    /// <summary>A well formed consumer handler.</summary>
    public class LaneConsumer
    {
        public Task<NatsAck> Handle(NatsMsg<LanePayload> msg, CancellationToken ct = default)
            => Task.FromResult(new NatsAck(true));
    }

    /// <summary>Handlers that violate the consumer contract.</summary>
    public class BadConsumer
    {
        public Task<NatsAck> WrongParameterCount(NatsMsg<LanePayload> msg) => Task.FromResult(new NatsAck(true));

        public Task<NatsAck> WrongFirstParameter(LanePayload payload, CancellationToken ct = default)
            => Task.FromResult(new NatsAck(true));

        public Task WrongReturnType(NatsMsg<LanePayload> msg, CancellationToken ct = default) => Task.CompletedTask;
    }

    /// <summary>A runtime source, as an application would implement it.</summary>
    public class LaneSource : INatsDynamicConsumerSource
    {
        public bool WasCalled { get; private set; }

        public async ValueTask<IReadOnlyCollection<NatsDynamicConsumer>> GetConsumersAsync(CancellationToken ct = default)
        {
            // Applications discover durables asynchronously (e.g. by listing them on a stream).
            await Task.Yield();
            WasCalled = true;
            return
            [
                NewConsumer("cbb.source.lane.1.>", "cbb-source-lane-1"),
                NewConsumer("cbb.source.lane.2.>", "cbb-source-lane-2")
            ];
        }
    }

    /// <summary>
    /// Emits throwaway assemblies carrying <see cref="NatsConsumerAttribute"/> decorated handlers so the
    /// assembly-scanning path can be exercised across more than one assembly.
    /// </summary>
    private static class EmittedConsumerAssembly
    {
        private static readonly Dictionary<string, Type> Emitted = new();
        private static readonly object Gate = new();

        public static void Ensure(string assemblyName, string subject, string consumerId)
        {
            lock (Gate)
            {
                if (Emitted.ContainsKey(assemblyName)) return;

                var assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName(assemblyName), AssemblyBuilderAccess.Run);
                var module = assembly.DefineDynamicModule(assemblyName);
                var type = module.DefineType($"{assemblyName}.EmittedConsumer", TypeAttributes.Public | TypeAttributes.Class);

                var method = type.DefineMethod(
                    "Handle",
                    MethodAttributes.Public,
                    typeof(Task<NatsAck>),
                    [typeof(NatsMsg<LanePayload>), typeof(CancellationToken)]);

                // The body is never invoked; these tests stop before Setup().
                var il = method.GetILGenerator();
                il.Emit(OpCodes.Ldnull);
                il.Emit(OpCodes.Ret);

                var attributeCtor = typeof(NatsConsumerAttribute).GetConstructor([typeof(string), typeof(string), typeof(string)])!;
                method.SetCustomAttribute(new CustomAttributeBuilder(attributeCtor, [subject, consumerId, ""]));

                Emitted[assemblyName] = type.CreateType();
            }
        }

        public static IEnumerable<Type> TypesFor(string assemblyNamePrefix)
        {
            lock (Gate)
            {
                return Emitted
                    .Where(kv => kv.Key.StartsWith(assemblyNamePrefix, StringComparison.Ordinal))
                    .Select(kv => kv.Value)
                    .ToArray();
            }
        }
    }

    #endregion helpers
}
