using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using CLOOPS.microservices;


namespace CLOOPS.NATS.Examples;

/// <summary>
/// Example showing how to use the consumer host to register consumer(s)
/// Sets up DI container
/// </summary>
public class NatsCoreConsumerHost
{
    public ICloopsNatsClient cnc;
    private IHost? _app;

    public NatsCoreConsumerHost()
    {
        cnc = new CloopsNatsClient(
                url: "nats://dev.nats.cloops.in:4222",  // Customize NATS server URL
                name: "Cloops Nats Example Client",     // Customize client name
                creds: null                             // Optional: provide credentials content if needed
            );
    }
    /// <summary>
    /// Demonstrates how to register and use NATS consumers
    /// </summary>
    public Task StartAsync()
    {
        if (_app is not null)
        {
            return Task.CompletedTask;
        }

        // create a host for DI
        var builder = Host.CreateApplicationBuilder();

        // setup logging
        builder.Logging.SetMinimumLevel(LogLevel.Information);

        // register nats client
        builder.Services.AddSingleton(cnc);

        // setup NatsLifecycleService to map consumers once nats is connected
        builder.Services.AddHostedService<NatsLifecycleService>();


        // Register all consumer classes
        builder.Services.AddSingleton<ConsumerExample>();

        // build and run app
        _app = builder.Build();
        return _app.StartAsync();
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
        => _app?.StopAsync(cancellationToken) ?? Task.CompletedTask;

    public Task WaitForShutdownAsync(CancellationToken cancellationToken = default)
        => _app?.WaitForShutdownAsync(cancellationToken) ?? Task.CompletedTask;
}
