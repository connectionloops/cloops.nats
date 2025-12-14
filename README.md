# CLOOPS NATS SDK

> the words events are messages are same and are used Interchangeably.

This SDK enables you to build reliable distributed systems using NATS messaging system. It enables annotation based definitions to control behavior of your NATS subscribers.

## Overview

Using NATS as communication layer for your distributed systems is quite impressive. You can implement sophisticated load balancing, fault tolerant systems that are location transparent and globally distributed.

- **Leaner than http** - It's a binary protocol
- **Less number of hops** - No load balancers, API gateways etc.
- **Globally distributed** - You can distribute your applications across the globe without any kind of DNS and load balancing complexity. Check out [NATS super cluster](https://docs.nats.io/running-a-nats-service/configuration/gateways) for additional details

Check out [this podcast from nats.fm](https://podcasts.apple.com/us/podcast/ep03-escaping-the-http-mindset-with-nats-io/id1700459773?i=1000625476010) for additional insights into this.

However, developers now a days expect annotation based definitions and dependency injection setups that are common across virtually any modern REST framework. With this project we are trying to supply similar framework to provide the same developer experience to teams that want to build their applications using NATS. Our hope is that this will reduce other differentiated work of consumer behavior handling.

This framework helps you with -

- Defining your NATS subscribers with annotations
- Provide back pressure in case of spikes
- Control load balancing strategies (i.e. broadcasting, load balancing)
- Works JetStream for building temporally decoupled systems.

> If you are building microservices then, we have a microservices focused SDK with all the bells and whistles built on top of cloops.nats. It is free and open source. Please check it out [here](https://github.com/connectionloops/cloops.microservices)

### Quick Examples

**Broadcast with unique pod names (for Kubernetes/Docker)**

To ensure each pod/instance receives all messages (broadcast), use runtime placeholders in the queue group name:

```cs
/// <summary>
/// Broadcast scenario: Each pod gets a unique queue group name, so all pods receive all messages
/// Supported placeholders: {POD_NAME}, {HOSTNAME}, {MACHINE_NAME}, {ENV:VAR_NAME}
/// </summary>
[NatsConsumer("test.broadcast", QueueGroupName = "pod-{POD_NAME}")]
public Task<NatsAck> BroadcastHandler(NatsMsg<string> msg, CancellationToken ct = default)
{
    Console.WriteLine($"[Pod {Environment.GetEnvironmentVariable("POD_NAME")}] message received: {msg.Data}");
    return Task.FromResult(new NatsAck(true));
}
```

The SDK resolves placeholders at runtime:

- `{POD_NAME}` - resolves to `POD_NAME` env var, or `HOSTNAME`, or machine name
- `{HOSTNAME}` - resolves to `HOSTNAME` env var, or machine name
- `{MACHINE_NAME}` - resolves to machine name
- `{ENV:VAR_NAME}` - resolves to any environment variable (e.g., `{ENV:MY_CUSTOM_VAR}`)

**Load balancing example**

Use same QueueGroup Name for multiple instances

```cs
[NatsConsumer("test.lb", _QueueGroupName: "workers")]
public async Task<NatsAck> HandleQueueGroup1(NatsMsg<string> msg, CancellationToken ct = default)
{
    Console.WriteLine($"LB - instance 1] message received: {msg.Data}");
    await Task.Delay(100, ct).ConfigureAwait(false); // Simulate work
    return new NatsAck(true);
}
```

> Note: \_QueueGroupName is optional, and if you do not provide one the empty string is used. It still makes consumers load balanced

> Note: JetStream subscriptions in cloops.nats are load balanced always (i.e. no broadcast)

### Installation

Add cloops.nats package reference to your `.csproj` file. Please note we deliberately want all of our consumers to stay on latest version of the SDK.

```xml
<PackageReference Include="cloops.nats" Version="*" />
```

Once added, just to `dotnet restore` to pull in the SDK.

> Please note: to get our Connection Loops standard messages, subjects etc., you might need to pull in `cloops.nats.schema`

## Quickstarts

Take a look at some examples [here](./examples)

## Documentation

For detailed documentation, setup instructions, and contribution guidelines:

📚 **[View Complete Documentation](./docs)**
