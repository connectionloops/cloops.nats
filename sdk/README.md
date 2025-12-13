# CLOOPS NATS SDK

The official Connection Loops SDK for reliable event-driven communication using NATS messaging system. This SDK provides strongly-typed event publishing and consumption capabilities for distributed microservices.

## Overview

Cloops NATS SDK will help you overcome some ofthe common problems in distributed systems communication to help build reliable systems. Some features include -

- **Strong typing** for all events, subjects and their associations
- **Compile-time safety** for event publishing
- **Well-defined patterns** for event governance
- **Opinionated conventions** specific to Connection Loops operations to help stay out of commomn pitfalls.

### Installation

Add cloops.nats package reference to your `.csproj` file. Please note we deliberately want all of our consumers to stay on latest version of the SDK.

```xml
<PackageReference Include="cloops.nats" Version="*" />
```

Once added, just to `dotnet restore` to pull in the SDK.

## Quickstarts

Take a look at some examples [here](https://dev.azure.com/clvc/MeAtConnectionLoops/_git/cloops.nats?path=/examples)

## Documentation

For detailed documentation, setup instructions, and contribution guidelines:

📚 **[View Complete Documentation](https://dev.azure.com/clvc/MeAtConnectionLoops/_git/cloops.nats?path=/docs)**

## Environment Variables used by SDK

- `NATS_SUBSCRIPTION_QUEUE_SIZE`
  - This vaiable defines what should be the max limmit of messages queued up for each subscription. Use this to control backpressure. Default: 10,000
