using System.Reflection;
using CLOOPS.NATS;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NATS.Client.Core;

namespace CLOOPS.microservices;

/// <summary>
/// Nats lifecycle service
/// Initializes the client connection
/// starts consumers to listen when connection is ready
/// </summary>
public class NatsLifecycleService : BackgroundService
{
    private readonly ICloopsNatsClient _client;
    private readonly ILogger<NatsLifecycleService> _logger;
    private readonly IServiceProvider _sp;

    /// <summary>
    /// Creates an instance of NatsLifecycleService Instance
    /// </summary>
    /// <param name="client">Nats Client</param>
    /// <param name="logger">Logger</param>
    /// <param name="sp"></param>
    /// <param name="appSettings"></param>
    public NatsLifecycleService(
        ICloopsNatsClient client,
        ILogger<NatsLifecycleService> logger,
        IServiceProvider sp
    )
    {
        _client = client;
        _logger = logger;
        _sp = sp;
    }

    /// <summary>
    /// Executes the service
    /// </summary>
    /// <param name="stoppingToken">Token to cancel the operation</param>
    /// <returns>A task that represents the asynchronous operation</returns>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await _client.ConnectAsync();

        if (_client.Connection.ConnectionState == NatsConnectionState.Open)
        {
            string assemblyName = Assembly.GetEntryAssembly()?.GetName().Name ?? AppDomain.CurrentDomain.FriendlyName ?? "unknown";
            // map all consumers from this assembly
            await _client.MapConsumers(_sp, stoppingToken, [assemblyName]);
        }
        else
        {
            _logger.LogError("NATS is not able to connect, cannot start consumers");
        }
    }
}