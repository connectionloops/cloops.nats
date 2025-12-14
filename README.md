# CLOOPS NATS SDK

[![CI](https://github.com/connectionloops/cloops.nats/actions/workflows/ci.yml/badge.svg)](https://github.com/connectionloops/cloops.nats/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/v/cloops.nats.svg)](https://www.nuget.org/packages/cloops.nats)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://opensource.org/licenses/MIT)

A modern, annotation-based SDK for building reliable distributed systems with NATS. Define your message consumers with simple attributes and let the framework handle the complexity.

## Why NATS?

NATS is a powerful messaging system that enables you to build sophisticated, fault-tolerant distributed systems that are location-transparent and globally distributed.

- **Faster than HTTP** - Binary protocol with lower overhead
- **Fewer hops** - Direct communication without load balancers or API gateways
- **Globally distributed** - Deploy applications worldwide without DNS or complex load balancing. Learn more about [NATS super clusters](https://docs.nats.io/running-a-nats-service/configuration/gateways)

> 💡 **Learn more**: Check out [this podcast from nats.fm](https://podcasts.apple.com/us/podcast/ep03-escaping-the-http-mindset-with-nats-io/id1700459773?i=1000625476010) for insights into NATS architecture.

## Why This SDK?

Modern developers expect annotation-based definitions and dependency injection—the same developer experience you get with REST frameworks like ASP.NET Core. This SDK brings that same simplicity to NATS, so you can focus on your business logic instead of boilerplate.

### What You Get

- ✨ **Annotation-based consumers** - Define subscribers with simple attributes
- 🛡️ **Built-in backpressure** - Automatic handling of traffic spikes
- ⚖️ **Flexible load balancing** - Choose between broadcasting or load balancing strategies
- 🚀 **JetStream support** - Build temporally decoupled systems with persistent messaging
- 🔧 **Dependency injection** - Seamless integration with .NET's DI container

> 🎯 **Building microservices?** Check out our [microservices-focused SDK](https://github.com/connectionloops/cloops.microservices) built on top of `cloops.nats` and makes building microservices a breeze!

## Quick Start

### Installation

Add the `cloops.nats` package to your `.csproj` file:

```xml
<PackageReference Include="cloops.nats" Version="*" />
```

Run `dotnet restore` to install the package.

> 💡 **Tip**: For Connection Loops standard messages and subjects, you may also need `cloops.nats.schema`.

### Examples

**Broadcast Pattern (Kubernetes/Docker)**

Ensure each pod/instance receives all messages by using runtime placeholders in the queue group name:

```cs
/// <summary>
/// Broadcast: Each pod gets a unique queue group, so all pods receive all messages
/// Supported placeholders: {POD_NAME}, {HOSTNAME}, {MACHINE_NAME}, {ENV:VAR_NAME}
/// </summary>
[NatsConsumer("test.broadcast", QueueGroupName = "pod-{POD_NAME}")]
public Task<NatsAck> BroadcastHandler(NatsMsg<string> msg, CancellationToken ct = default)
{
    Console.WriteLine($"[Pod {Environment.GetEnvironmentVariable("POD_NAME")}] Received: {msg.Data}");
    return Task.FromResult(new NatsAck(true));
}
```

**Load Balancing Pattern**

Distribute messages across multiple instances using the same queue group:

```cs
[NatsConsumer("test.lb", QueueGroupName = "workers")]
public async Task<NatsAck> HandleMessage(NatsMsg<string> msg, CancellationToken ct = default)
{
    Console.WriteLine($"Instance received: {msg.Data}");
    await Task.Delay(100, ct).ConfigureAwait(false); // Simulate work
    return new NatsAck(true);
}
```

**Runtime Placeholders**

The SDK resolves placeholders dynamically:

- `{POD_NAME}` → `POD_NAME` env var, falls back to `HOSTNAME` or machine name
- `{HOSTNAME}` → `HOSTNAME` env var, falls back to machine name
- `{MACHINE_NAME}` → Machine name
- `{ENV:VAR_NAME}` → Any environment variable (e.g., `{ENV:MY_CUSTOM_VAR}`)

> 📝 **Note**: `QueueGroupName` is optional. If omitted, an empty string is used, which still enables load balancing. JetStream subscriptions are always load-balanced (no broadcast support).

## Learn More

- 📖 **[Examples](./examples)** - See real-world usage patterns
- 📚 **[Full Documentation](./docs)** - Detailed guides, setup instructions, and API reference

## Contributing

Contributions are welcome! Please feel free to submit a Pull Request.

## License

This project is licensed under the MIT License - see the [LICENSE](LICENSE) file for details.
