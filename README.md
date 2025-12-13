# CLOOPS NATS SDK

> the words events are messages are same and are used Interchangeably.

This SDK enables you to build reliable distributed systems using NATS messaging system. It enables annotation based definitions to control behavior of your subscribers (and responders)

## Overview

Using NATS as communication layer for your distributed systems is quite impressive. You can implement sophisticated load balancing, fault tolerant systems that are location transparent and globally distributed.

- **Leaner than http** - It's a binary protocol
- **Less number of hops** - No load balancers, API gateways etc.
- **Globally distributed** - You can distribute your applications across the globe without any kind of DNS and load balancing complexity. Check out [NATS super cluster](https://docs.nats.io/running-a-nats-service/configuration/gateways) for additional details

Check out [this podcast from nats.fm](https://podcasts.apple.com/us/podcast/ep03-escaping-the-http-mindset-with-nats-io/id1700459773?i=1000625476010) for additional insights into this.

However, developers now a days take annotation based definitions and dependency injection setups that are common across virtually any modern REST framework for granted. With this project we are trying to supply similar framework to provide the same developer experience to teams that want to build their applications using NATS instead of REST.

This framework helps you with -

- Defining your NATS subscribers and responders with annotations
- Handle backpressure in case of spikes
- Control load balancing strategies (i.e. broadcast vs load balance)
- Supports JetStream to build temporally decoupled systems.

> If you are building microservices then, we have a microservices focused SDK built on top of cloops.nats. It is free and open source. Please check it out [here](https://github.com/connectionloops/cloops.microservices)

### Installation

Add cloops.nats package reference to your `.csproj` file. Please note we deliberately want all of our consumers to stay on latest version of the SDK.

```xml
<PackageReference Include="cloops.nats" Version="*" />
```

Once added, just to `dotnet restore` to pull in the SDK.

> Please note: to get our standard messages, subjects etc., you might need to pull in `cloops.nats.schema`

## Quickstarts

Take a look at some examples [here](./examples/)

## Documentation

For detailed documentation, setup instructions, and contribution guidelines:

📚 **[View Complete Documentation](./docs/)**
