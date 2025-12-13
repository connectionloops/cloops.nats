using System.Threading.Channels;
using NATS.Client.Core;
using NATS.Client.JetStream;
using NATS.Net;
using System.Reflection;
using CLOOPS.NATS.Attributes;
using CLOOPS.NATS.Serialization;
using NATS.Client.KeyValueStore;
using CLOOPS.NATS.Locking;

namespace CLOOPS.NATS;



/// <summary>
/// Interface for Cloops Nats Client
/// </summary>
public interface ICloopsNatsClient : INatsClient
{
    /// <summary>
    /// Creates a JetStream Context
    /// </summary>
    /// <returns></returns>
    public INatsJSContext CreateJetStreamContext();

    /// <summary>
    /// Creates a JetStream Context
    /// </summary>
    /// <param name="opts"></param>
    /// <returns></returns>
    public INatsJSContext CreateJetStreamContext(NatsJSOpts opts);

    /// <summary>
    /// Create a KV context
    /// </summary>
    /// <returns></returns>
    public INatsKVContext CreateKVContext();

    /// <summary>
    /// Create a KV context
    /// </summary>
    /// <param name="opts"></param>
    /// <returns></returns>
    public INatsKVContext CreateKVContext(NatsKVOpts opts);

    /// <summary>
    /// Scans loaded assemblies (optionally filtered by simple name) and registers all NATS consumers
    /// decorated with <see cref="NatsConsumerAttribute"/>.
    /// </summary>
    /// <param name="sp">Service provider.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="assemblyNameFilters">Optional assembly simple name filters (exact or prefix, case-insensitive). If omitted / empty, scans all loaded assemblies.</param>
    /// <param name="throwOnDuplicate">If true, an exception will be thrown if a duplicate consumer is found. If false, the duplicate consumer will be ignored.</param>
    public Task MapConsumers(IServiceProvider sp, CancellationToken ct = default, string[]? assemblyNameFilters = null, bool throwOnDuplicate = true);

    /// <summary>
    /// Sets Up All the KV Stores
    /// </summary>
    /// <param name="ct">Cancellation token</param>
    /// <returns></returns>
    public Task SetupKVStoresAsync(CancellationToken ct = default);


    /// <summary>
    /// Tries to acquire a distributed lock for given key
    /// If lock is acquired, a lock handle is returned
    /// otherwise null
    /// </summary>
    /// <param name="key">key to get lock for</param>
    /// <param name="timeout">keep trying for this timeout. default: 1.5s</param>
    /// <param name="ownerId">who are you. defaults to assembly plus host</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns></returns>
    public Task<DistributedLockHandle?> AcquireDistributedLockAsync(string key, TimeSpan? timeout = null, string? ownerId = null, CancellationToken ct = default);
}

/// <summary>
/// Cloops NATS Client. Use this to connect to NATS
/// Implements same interface as official NATS Client
/// More Info: https://nats-io.github.io/nats.net/documentation/intro.html?tabs=core-nats
/// </summary>
public class CloopsNatsClient : ICloopsNatsClient
{
    /// <inheritdoc />
    public INatsConnection Connection { get; }

    /// <summary>
    /// Default JS context
    /// </summary>
    public INatsJSContext JsContext { get; }

    /// <summary>
    /// Default KV context
    /// </summary>
    public INatsKVContext KvContext { get; }

    internal KvDistributedLock? Locker { get; set; }

    /// <summary>
    /// Initializes a new instance of the <see cref="NatsClient"/> class.
    /// </summary>
    /// <param name="url">NATS server URL to connect to. (default: nats://localhost:4222)</param>
    /// <param name="name">Client name. (default: NATS .NET Client)</param>
    /// <param name="creds">The content of the creds for NATS Connection</param>
    /// <remarks>
    /// <para>
    /// You can set more than one server as seed servers in a comma-separated list in the <paramref name="url"/>.
    /// The client will randomly select a server from the list to connect.
    /// </para>
    /// <para>
    /// User-password or token authentication can be set in the <paramref name="url"/>.
    /// For example, <c>nats://derek:s3cr3t@localhost:4222</c> or <c>nats://token@localhost:4222</c>.
    /// You should URL-encode the username and password or token if they contain special characters.
    /// </para>
    /// <para>
    /// If multiple servers are specified and user-password or token authentication is used in the <paramref name="url"/>,
    /// only the credentials in the first server URL will be used; credentials in the remaining server
    /// URLs will be ignored.
    /// </para>
    /// </remarks>
    public CloopsNatsClient(
        string url = "nats://dev.nats.cloops.in:4222",
        string name = "CLOOPS NATS .NET Client",
        string? creds = null)
    {
        var opts = new NatsOpts
        {
            Name = name,
            Url = url,
            SerializerRegistry = new CloopsSerializerRegistry(),
            SubPendingChannelFullMode = BoundedChannelFullMode.Wait,
            AuthOpts = new NatsAuthOpts { Creds = creds },
        };
        Connection = new NatsConnection(opts);
        JsContext = Connection.CreateJetStreamContext();
        KvContext = Connection.CreateKeyValueStoreContext();
    }

    /// <inheritdoc />
    public ValueTask ConnectAsync() => Connection.ConnectAsync();

    /// <inheritdoc />
    public ValueTask<TimeSpan> PingAsync(CancellationToken cancellationToken = default) => Connection.PingAsync(cancellationToken);

    /// <inheritdoc />
    public ValueTask PublishAsync<T>(string subject, T data, NatsHeaders? headers = default, string? replyTo = default, INatsSerialize<T>? serializer = default, NatsPubOpts? opts = default, CancellationToken cancellationToken = default)
        => Connection.PublishAsync(subject, data, headers, replyTo, serializer, opts, cancellationToken);

    /// <inheritdoc />
    public ValueTask PublishAsync(string subject, NatsHeaders? headers = default, string? replyTo = default, NatsPubOpts? opts = default, CancellationToken cancellationToken = default)
        => Connection.PublishAsync(subject, headers, replyTo, opts, cancellationToken);

    /// <inheritdoc />
    public IAsyncEnumerable<NatsMsg<T>> SubscribeAsync<T>(string subject, string? queueGroup = default, INatsDeserialize<T>? serializer = default, NatsSubOpts? opts = default, CancellationToken cancellationToken = default)
        => Connection.SubscribeAsync(subject, queueGroup, serializer, opts, cancellationToken);

    /// <inheritdoc />
    public ValueTask<NatsMsg<TReply>> RequestAsync<TRequest, TReply>(string subject, TRequest? data, NatsHeaders? headers = default, INatsSerialize<TRequest>? requestSerializer = default, INatsDeserialize<TReply>? replySerializer = default, NatsPubOpts? requestOpts = default, NatsSubOpts? replyOpts = default, CancellationToken cancellationToken = default)
        => Connection.RequestAsync(subject, data, headers, requestSerializer, replySerializer, requestOpts, replyOpts, cancellationToken);

    /// <inheritdoc />
    public ValueTask<NatsMsg<TReply>> RequestAsync<TReply>(string subject, INatsDeserialize<TReply>? replySerializer = default, NatsSubOpts? replyOpts = default, CancellationToken cancellationToken = default)
        => Connection.RequestAsync(subject, replySerializer, replyOpts, cancellationToken);

    /// <inheritdoc />
    public ValueTask ReconnectAsync() => Connection.ReconnectAsync();

    /// <inheritdoc />
    public async Task SetupKVStoresAsync(CancellationToken ct = default)
    {
        var locksStoreContext = await KvContext.GetStoreAsync("locks", ct).ConfigureAwait(false);
        Locker = new KvDistributedLock(locksStoreContext);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => Connection.DisposeAsync();


    /// <inheritdoc />
    public INatsJSContext CreateJetStreamContext()
    {
        return new NatsJSContext(Connection);
    }

    /// <inheritdoc />
    public INatsJSContext CreateJetStreamContext(NatsJSOpts opts)
    {
        return new NatsJSContext(Connection, opts);
    }

    /// <inheritdoc />
    public INatsKVContext CreateKVContext(NatsKVOpts opts)
    {
        return new NatsKVContext(JsContext, opts);
    }

    /// <inheritdoc />
    public INatsKVContext CreateKVContext()
    {
        return new NatsKVContext(JsContext);
    }

    /// <summary>
    /// Discovers and registers NATS consumer methods decorated with <see cref="NatsConsumerAttribute"/>.
    /// </summary>
    /// <param name="sp">Service provider used to resolve any dependencies required by consumer containing types.</param>
    /// <param name="ct">Cancellation token to abort discovery/registration.</param>
    /// <param name="assemblyNameFilters">Optional assembly simple names or prefixes (case-insensitive). When supplied, only assemblies whose simple name equals or starts with one of the filters are scanned.</param>
    /// <param name="throwOnDuplicate">If true, an exception will be thrown if a duplicate consumer is found. If false, the duplicate consumer will be ignored.</param>
    /// <remarks>
    /// <para>Performance considerations: The method limits reflection cost by (1) filtering assemblies early, (2) using <see cref="MemberInfo.IsDefined(System.Type,bool)"/> for a fast attribute existence check before instantiation, (3) restricting method lookup to <c>DeclaredOnly</c> to avoid inherited duplication, and (4) gracefully handling partial type load failures.</para>
    /// <para>Idempotency: Repeated calls will create additional subscriptions; typically call once during startup.</para>
    /// </remarks>
    public async Task MapConsumers(IServiceProvider sp, CancellationToken ct = default, string[]? assemblyNameFilters = null, bool throwOnDuplicate = true)
    {
        // Choose target assemblies (filter if provided)
        var allAssemblies = AppDomain.CurrentDomain.GetAssemblies();
        var targetAssemblies = (assemblyNameFilters != null && assemblyNameFilters.Length > 0)
            ? allAssemblies.Where(a =>
                {
                    var name = a.GetName().Name ?? string.Empty;
                    return assemblyNameFilters.Any(f =>
                        name.Equals(f, StringComparison.OrdinalIgnoreCase) ||
                        name.StartsWith(f, StringComparison.OrdinalIgnoreCase));
                })
                .ToArray()
            : allAssemblies;

        var registrationTasks = new List<Task>();
        var subjectSet = new HashSet<string>();

        foreach (var assembly in targetAssemblies)
        {
            IEnumerable<Type> types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                types = ex.Types.Where(t => t != null)!; // Skip types that failed to load
            }

            // one nats subscription processor per consumer id (for all subjects it represents)
            Dictionary<string, NatsSubscriptionProcessor> consumerIdToSubscriptionProcessor = new();
            foreach (var type in types)
            {
                // Restrict binding flags to declared methods only to avoid scanning inherited base methods repeatedly
                var methods = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly);
                foreach (var method in methods)
                {
                    if (!method.IsDefined(typeof(NatsConsumerAttribute), inherit: false))
                        continue; // quick filter without creating the attribute instance

                    var consumerAttr = method.GetCustomAttribute<NatsConsumerAttribute>();
                    if (consumerAttr == null)
                        continue; // safety

                    if (subjectSet.Contains(consumerAttr.Subject) && throwOnDuplicate)
                    {
                        Environment.FailFast($"Duplicate consumer found for subject {consumerAttr.Subject} in {assembly.GetName().Name}. Please make sure you have only one consumer per subject.");
                    }

                    subjectSet.Add(consumerAttr.Subject);
                    // Create subscription processor and queue setup task
                    consumerIdToSubscriptionProcessor.TryGetValue(consumerAttr.ConsumerId, out var sub);
                    if (sub == null)
                    {
                        sub = new NatsSubscriptionProcessor(sp, this, consumerAttr.ConsumerId, _IsDurable: consumerAttr.IsDurable);
                        consumerIdToSubscriptionProcessor.Add(consumerAttr.ConsumerId, sub);
                    }
                    sub.AddSubect(consumerAttr.Subject, consumerAttr, type, method);
                }
            }
            foreach (var sub in consumerIdToSubscriptionProcessor.Values)
            {
                registrationTasks.Add(sub.Setup(ct));
            }
        }

        // Await all registration tasks
        if (registrationTasks.Count > 0)
        {
            await Task.WhenAll(registrationTasks).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Tries to acquire lock for given key
    /// </summary>
    /// <param name="key">key to get lock on</param>
    /// <param name="timeout">Keep trying until</param>
    /// <param name="ownerId">who are you. defaults to assembly plus host</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Lock handle if lock is acquired, null otherewise</returns>
    /// <exception cref="InvalidOperationException">If setup is incorrect</exception>
    public async Task<DistributedLockHandle?> AcquireDistributedLockAsync(string key, TimeSpan? timeout = null, string? ownerId = null, CancellationToken ct = default)
    {
        if (Locker == null)
        {
            await SetupKVStoresAsync(ct).ConfigureAwait(false);
        }

        if (Locker == null)
        {
            // something went wrong while setting up the locker
            throw new InvalidOperationException("Locker KV store is not set. Please make sure you have called `SetupKVStoresAsync` in your lifecycle");
        }

        return await Locker.TryAcquireAsync(key, timeout, ownerId, ct).ConfigureAwait(false);
    }
}
