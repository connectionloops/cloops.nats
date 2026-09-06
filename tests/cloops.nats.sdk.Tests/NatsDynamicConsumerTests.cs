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


    [Fact]
    public async Task BuildSubscriptionProcessors_AttributePath_ThrowsOnDuplicateSubject_WhenConsumerIdIsShared()
    {
        // Characterisation of PRE-EXISTING behaviour, not something this change introduced.
        // throwOnDuplicate:false does NOT skip the duplicate on the attribute path: the subject is bound
        // again on the same processor and the inner dictionary rejects it.
        const string prefix = "CloopsNatsDupeSameIdProbe";
        EmittedConsumerAssembly.Ensure($"{prefix}.A", "probe.dupe.same", "probe-dupe-same");
        EmittedConsumerAssembly.Ensure($"{prefix}.B", "probe.dupe.same", "probe-dupe-same");

        await using var client = NewClient();
        using var sp = NewServiceProvider(services =>
        {
            foreach (var type in EmittedConsumerAssembly.TypesFor(prefix)) services.AddSingleton(type);
        });

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            client.BuildSubscriptionProcessors(sp, [prefix], throwOnDuplicate: false, dynamicConsumers: null, CancellationToken.None));

        Assert.Contains("same key has already been added", ex.Message);
    }

    [Fact]
    public async Task BuildSubscriptionProcessors_AttributePath_SilentlyDoubleBinds_WhenConsumerIdsDiffer()
    {
        // Characterisation of PRE-EXISTING behaviour: with throwOnDuplicate:false and different consumer
        // ids, the same subject is bound to two processors and is delivered to both.
        const string prefix = "CloopsNatsDupeDiffIdProbe";
        EmittedConsumerAssembly.Ensure($"{prefix}.A", "probe.dupe.diff", "probe-dupe-a");
        EmittedConsumerAssembly.Ensure($"{prefix}.B", "probe.dupe.diff", "probe-dupe-b");

        await using var client = NewClient();
        using var sp = NewServiceProvider(services =>
        {
            foreach (var type in EmittedConsumerAssembly.TypesFor(prefix)) services.AddSingleton(type);
        });

        var processors = await client.BuildSubscriptionProcessors(
            sp, [prefix], throwOnDuplicate: false, dynamicConsumers: null, CancellationToken.None);

        Assert.Equal(2, processors.Count);
        Assert.All(processors.Values, p => Assert.Equal(["probe.dupe.diff"], p.RegisteredSubjects));
    }

    #endregion assembly scan

    #region dispatch

    [Fact]
    public async Task DynamicConsumer_CompilesAndDispatchesToTheBoundHandler()
    {
        // Coverage past processor construction: this runs everything Setup() does except opening the
        // subscription - payload type validation, invoker compilation and handler dispatch. Opening the
        // subscription itself needs a live NATS server and is not covered by this suite.
        await using var client = NewClient();
        using var sp = NewServiceProvider();

        var processors = await client.BuildSubscriptionProcessors(
            sp,
            NoAssemblies,
            throwOnDuplicate: true,
            dynamicConsumers: [NewConsumer("cbb.lane.7.>", "cbb-lane-7")],
            CancellationToken.None);

        var processor = Assert.Single(processors).Value;
        processor.BuildInvocationPlans();

        var plan = processor.GetInvocationPlan("cbb.lane.7.>");
        Assert.Equal(typeof(LanePayload), plan.PayloadType);
        Assert.Equal(nameof(LaneConsumer.Handle), plan.HandlerName);

        var msg = new NatsMsg<LanePayload>(
            subject: "cbb.lane.7.work",
            replyTo: null,
            size: 0,
            headers: null,
            data: new LanePayload { Id = "lane-7" },
            connection: null!,
            flags: default);

        var ack = await plan.Invoke(msg, CancellationToken.None);

        Assert.True(ack.IsAcknowledged);
        Assert.Equal("lane-7", ack.Reply);
        Assert.Equal("lane-7", sp.GetRequiredService<LaneConsumer>().LastSeenId);
    }

    [Fact]
    public async Task BuildInvocationPlans_CompilesEveryLaneBoundToOneHandler()
    {
        await using var client = NewClient();
        using var sp = NewServiceProvider();

        var lanes = Enumerable.Range(1, 3)
            .Select(i => NewConsumer($"cbb.lane.{i}.>", $"cbb-lane-{i}"))
            .ToArray();

        var processors = await client.BuildSubscriptionProcessors(
            sp, NoAssemblies, throwOnDuplicate: true, dynamicConsumers: lanes, CancellationToken.None);

        foreach (var (consumerId, processor) in processors)
        {
            processor.BuildInvocationPlans();
            var subject = processor.RegisteredSubjects.Single();
            var plan = processor.GetInvocationPlan(subject);

            var msg = new NatsMsg<LanePayload>(
                subject: subject.Replace(">", "work"),
                replyTo: null,
                size: 0,
                headers: null,
                data: new LanePayload { Id = consumerId },
                connection: null!,
                flags: default);

            var ack = await plan.Invoke(msg, CancellationToken.None);
            Assert.Equal(consumerId, ack.Reply);
        }
    }

    #endregion dispatch

    #region discovery timeout

    [Fact]
    public async Task BuildSubscriptionProcessors_Throws_WhenSourceExceedsDiscoveryTimeout()
    {
        using var _ = new EnvVar("NATS_DYNAMIC_CONSUMER_DISCOVERY_TIMEOUT_SECONDS", "1");
        await using var client = NewClient();
        using var sp = NewServiceProvider(services => services.AddNatsDynamicConsumerSource<HangingSource>());

        var ex = await Assert.ThrowsAsync<TimeoutException>(() =>
            client.BuildSubscriptionProcessors(sp, NoAssemblies, throwOnDuplicate: true, dynamicConsumers: null, CancellationToken.None));

        Assert.Contains(nameof(HangingSource), ex.Message);
        Assert.Contains("NATS_DYNAMIC_CONSUMER_DISCOVERY_TIMEOUT_SECONDS", ex.Message);
    }

    [Fact]
    public async Task BuildSubscriptionProcessors_Throws_WhenSourceIgnoresItsCancellationToken()
    {
        using var _ = new EnvVar("NATS_DYNAMIC_CONSUMER_DISCOVERY_TIMEOUT_SECONDS", "1");
        await using var client = NewClient();
        using var sp = NewServiceProvider(services => services.AddNatsDynamicConsumerSource<TokenIgnoringSource>());

        var ex = await Assert.ThrowsAsync<TimeoutException>(() =>
            client.BuildSubscriptionProcessors(sp, NoAssemblies, throwOnDuplicate: true, dynamicConsumers: null, CancellationToken.None));

        Assert.Contains("did not honour its cancellation token", ex.Message);
    }

    [Fact]
    public async Task BuildSubscriptionProcessors_PropagatesCallerCancellation_AsOperationCanceled()
    {
        // A cancelled host shutdown must not be misreported as a discovery timeout.
        using var _ = new EnvVar("NATS_DYNAMIC_CONSUMER_DISCOVERY_TIMEOUT_SECONDS", "600");
        await using var client = NewClient();
        using var sp = NewServiceProvider(services => services.AddNatsDynamicConsumerSource<HangingSource>());
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.BuildSubscriptionProcessors(sp, NoAssemblies, throwOnDuplicate: true, dynamicConsumers: null, cts.Token));
    }

    [Fact]
    public async Task BuildSubscriptionProcessors_DoesNotTimeOutAPromptSource()
    {
        using var _ = new EnvVar("NATS_DYNAMIC_CONSUMER_DISCOVERY_TIMEOUT_SECONDS", "30");
        await using var client = NewClient();
        using var sp = NewServiceProvider(services => services.AddNatsDynamicConsumerSource<LaneSource>());

        var processors = await client.BuildSubscriptionProcessors(
            sp, NoAssemblies, throwOnDuplicate: true, dynamicConsumers: null, CancellationToken.None);

        Assert.Equal(2, processors.Count);
    }

    [Fact]
    public void GetDynamicConsumerDiscoveryTimeout_DefaultsTo30Seconds()
    {
        using var _ = new EnvVar("NATS_DYNAMIC_CONSUMER_DISCOVERY_TIMEOUT_SECONDS", null);

        Assert.Equal(30, CloopsNatsClient.DefaultDynamicConsumerDiscoveryTimeoutSeconds);
        Assert.Equal(TimeSpan.FromSeconds(30), CloopsNatsClient.GetDynamicConsumerDiscoveryTimeout());
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    public void GetDynamicConsumerDiscoveryTimeout_CanBeDisabled(string value)
    {
        using var _ = new EnvVar("NATS_DYNAMIC_CONSUMER_DISCOVERY_TIMEOUT_SECONDS", value);

        Assert.Equal(Timeout.InfiniteTimeSpan, CloopsNatsClient.GetDynamicConsumerDiscoveryTimeout());
    }

    [Fact]
    public void GetDynamicConsumerDiscoveryTimeout_FallsBackToDefault_WhenValueIsNotAnInteger()
    {
        using var _ = new EnvVar("NATS_DYNAMIC_CONSUMER_DISCOVERY_TIMEOUT_SECONDS", "not-a-number");

        Assert.Equal(TimeSpan.FromSeconds(30), CloopsNatsClient.GetDynamicConsumerDiscoveryTimeout());
    }

    #endregion discovery timeout

    #region overloads

    [Theory]
    [InlineData(typeof(ICloopsNatsClient))]
    [InlineData(typeof(CloopsNatsClient))]
    public void MapConsumers_KeepsTheFourParameterMember(Type declaringType)
    {
        // Binary compatibility guard. C# binds optional arguments at the call site, so turning the four
        // parameter method into a five parameter one with a default would delete this member reference and
        // break already-compiled callers (cloops.microservices 1.1.25 calls exactly this signature) with a
        // MissingMethodException at consumer startup. The new argument must stay a separate overload.
        var fourParam = declaringType.GetMethod(
            nameof(ICloopsNatsClient.MapConsumers),
            [typeof(IServiceProvider), typeof(CancellationToken), typeof(string[]), typeof(bool)]);

        Assert.NotNull(fourParam);
        Assert.Equal(typeof(Task), fourParam!.ReturnType);

        var fiveParam = declaringType.GetMethod(
            nameof(ICloopsNatsClient.MapConsumers),
            [typeof(IServiceProvider), typeof(CancellationToken), typeof(string[]), typeof(bool), typeof(IEnumerable<NatsDynamicConsumer>)]);

        Assert.NotNull(fiveParam);

        // The added parameter must NOT be optional, otherwise a four argument call becomes ambiguous.
        Assert.False(fiveParam!.GetParameters()[4].IsOptional);
    }

    [Fact]
    public async Task MapConsumers_FourArgumentOverload_DelegatesToTheNewOne()
    {
        await using var client = NewClient();
        using var sp = NewServiceProvider();
        ICloopsNatsClient asInterface = client;

        // Every arity an existing caller may have compiled against, minus the unfiltered ones: scanning
        // every loaded assembly trips over the test platform's own assemblies (pre-existing behaviour).
        await asInterface.MapConsumers(sp, CancellationToken.None, NoAssemblies, true);
        await asInterface.MapConsumers(sp, CancellationToken.None, NoAssemblies);
        await asInterface.MapConsumers(sp, CancellationToken.None, NoAssemblies, true, null);
    }

    #endregion overloads

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
        public string? LastSeenId { get; private set; }

        public Task<NatsAck> Handle(NatsMsg<LanePayload> msg, CancellationToken ct = default)
        {
            LastSeenId = msg.Data?.Id;
            return Task.FromResult(new NatsAck(true, msg.Data?.Id));
        }
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

    /// <summary>A source that never returns, honouring its cancellation token.</summary>
    public class HangingSource : INatsDynamicConsumerSource
    {
        public async ValueTask<IReadOnlyCollection<NatsDynamicConsumer>> GetConsumersAsync(CancellationToken ct = default)
        {
            await Task.Delay(Timeout.Infinite, ct);
            return [];
        }
    }

    /// <summary>A source that overruns its budget and ignores the cancellation token it was given.</summary>
    public class TokenIgnoringSource : INatsDynamicConsumerSource
    {
        public async ValueTask<IReadOnlyCollection<NatsDynamicConsumer>> GetConsumersAsync(CancellationToken ct = default)
        {
            await Task.Delay(TimeSpan.FromSeconds(2), CancellationToken.None);
            return [];
        }
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
