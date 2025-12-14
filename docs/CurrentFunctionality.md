# CLOOPS NATS SDK - Current Functionality

This document outlines the currently available features and capabilities of the CLOOPS NATS SDK. The SDK is designed to provide type-safe, reliable messaging patterns for distributed microservices.

## 📑 Index

Quick navigation to key features:

- [Architecture Overview](#️-architecture-overview)
- [Available Features](#️-available-features)
  - [Message Subscription & Consumption](#-message-subscription--consumption)
  - [JetStream Durable Consumers](#-jetstream-durable-consumers)
  - [High-Performance Processing](#-high-performance-processing)
  - [Distributed Locking](#-distributed-locking)
  - [Queue Groups Implementation](#-queue-groups-implementation)
  - [Request-Reply Communication](#-request-reply-communication)
  - [Metrics & Observability](#-metrics--observability)
  - [Token Minting Service](#-token-minting-service)
  - [Subject Builders (External Package)](#-subject-builders-external-package)
  - [NATS Core Publishing](#-nats-core-publishing)
  - [JetStream Publishing](#-jetstream-publishing)
- [SDK Components](#-sdk-components)
- [Example Workflows](#-example-workflows)
- [Work In Progress](#-work-in-progress)

## 🏗️ Architecture Overview

The SDK provides a framework for building reliable, type-safe NATS messaging applications with a clean separation of concerns:

- **Client Layer**: High-level API for connecting to NATS and performing operations (publish, subscribe, request-reply)
- **Consumer Framework**: Attribute-driven message consumption with automatic routing and type-safe handling
- **Transport Abstraction**: Seamless support for both NATS Core and JetStream protocols
- **Infrastructure Services**: Built-in support for metrics collection, distributed locking, and token minting
- **Serialization**: JSON serialization with customizable options for message encoding/decoding

**Note**: Message schemas, subject builders, and subject definitions are defined externally (typically in a separate schema package). The SDK works with any strongly-typed message types and subject strings.

## ✅ Available Features

### 📥 Message Subscription & Consumption

- **Attribute-driven**: Use `[NatsConsumer]` to mark methods as message handlers
- **Automatic routing**: SDK automatically routes messages to appropriate handlers
- **Type safety**: Strongly-typed message handling with compile-time validation
- **Queue groups**: Built-in load balancing across multiple consumer instances
- **Dual protocol support**: Seamless handling of both NATS Core and JetStream messages
- **Automatic message validation**: If a message type has a `Validate()` method, the SDK automatically validates messages before processing. Invalid messages are discarded (JetStream messages are terminated, Core NATS messages are skipped).
- **Explicit ACK/NAK contract**: **All handlers** must return `Task<NatsAck>`. JetStream uses the result to `Ack` on `Success` or `Nak` on `Fail`; Core ignores it.
- **Handler signature enforcement**: The SDK validates at startup that handlers return `Task<NatsAck>`, preventing misconfiguration.
- **Automatic ACK/NAK (JetStream)**: After handler execution, the processor calls `AckAsync` or `NakAsync` based on the handler’s result, guaranteeing at-least-once delivery.
- **Exception safety**: If a handler throws, the processor logs the error and issues a NAK (JetStream), enabling retry/DLQ per configuration.

**Example**:

```csharp
[NatsConsumer("events.process")]
public async Task<NatsAck> HandleEvent(NatsMsg<Event> msg, CancellationToken ct)
{
    await ProcessEvent(msg.Data);
    return NatsAck.Success;
}
```

### 📥 JetStream Durable Consumers

**Purpose**: Enterprise-grade message consumption with persistence and at-least-once delivery.

**Key Features (Updated):**

- **Control-plane ownership**: Streams and durable consumers are provisioned via GitOps. At runtime the SDK **does not create/update** them. It:

  - Resolves the stream **by subject** (`ListStreamNamesAsync(subject)`), then
  - **Attaches** to the existing consumer with `GetConsumerAsync(streamName, consumerId)`.

- **Durable persistence**: Messages remain in the stream until the consumer **explicitly ACKs** them (consumer AckPolicy must be Explicit).

- **At-least-once delivery**: With durable consumers and explicit acks, JetStream redelivers until ACKed (subject to consumer/stream config).

- **Explicit ACK/NAK contract**: All handlers must return `Task<NatsAck>`.

  - `NatsAck.Success` → SDK calls `AckAsync`
  - `NatsAck.Fail` or exceptions → SDK calls `NakAsync` (JetStream retry behavior applies)

- **Retry behavior**: Redelivery timing and limits are controlled by JetStream consumer/stream config (e.g., MaxDeliver, Backoff, DLQ policies).

**Example**:

```csharp
[NatsConsumer("CP.*.EffectTriggered.Durable", _consumerId: "effect-durable")]
public async Task<NatsAck> ProcessDurableEffect(NatsMsg<EffectTriggered> msg, CancellationToken ct)
{
    try
    {
        await ProcessCriticalEffect(msg.Data);
        return NatsAck.Success; // SDK will Ack
    }
    catch (Exception ex)
    {
        return NatsAck.Fail; // SDK will Nak (JetStream retries per config)
    }
}
```

### ⚡ High-Performance Processing

**Purpose**: Optimized message processing with configurable parallelism and backpressure control.

**Key Features**:

- **Configurable parallelism**: Control concurrent message processing via environment variables
- **Backpressure management**: Bounded queues prevent memory overflow
- **Resource management**: Environment-based configuration for production flexibility

**Configuration via Environment Variables**:

The SDK supports high-performance processing through environment variables (see [Environment Variables Documentation](./EnvironmentVariables.md)):

- **`NATS_CONSUMER_MAX_DOP`**: Maximum degree of parallelism (default: 128)
  - Controls how many messages can be processed concurrently
  - Higher values increase throughput but require more CPU/memory
- **`NATS_SUBSCRIPTION_QUEUE_SIZE`**: Maximum queue capacity per subscription (default: 20,000)
  - Controls backpressure when processing is slower than message arrival
  - When full, the SDK applies backpressure to prevent memory overflow

**Example Consumer**:

```csharp
[NatsConsumer("analytics.events")]
public async Task<NatsAck> ProcessAnalytics(NatsMsg<AnalyticsEvent> msg, CancellationToken ct = default)
{
    // Processing happens with parallelism and backpressure controlled by environment variables
    await analyticsService.ProcessEvent(msg.Data);
    return NatsAck.Success;
}
```

**Performance Tuning**:

- For high-throughput scenarios, increase `NATS_CONSUMER_MAX_DOP` (e.g., 200-500)
- Monitor queue depth; if it consistently reaches capacity, either:
  - Increase processing speed (optimize handlers)
  - Increase `NATS_SUBSCRIPTION_QUEUE_SIZE` (with more memory)
  - Scale horizontally (more consumer instances)

### 🔒 Distributed Locking

**Purpose**: Provide distributed locking capabilities using NATS Key-Value stores for coordination across multiple service instances.

**Key Features**:

- **Distributed coordination**: Lock resources across multiple service instances
- **Automatic cleanup**: Locks are automatically released when the handle is disposed
- **Timeout support**: Configurable timeout for lock acquisition attempts
- **Owner identification**: Track which instance holds the lock
- **Resource isolation**: Different keys provide independent locks

**Quick Example**:

```csharp
var cnc = new CloopsNatsClient();
await cnc.SetupKVStoresAsync();

var lockHandle = await cnc.AcquireDistributedLockAsync("my-resource");
if (lockHandle != null)
{
    await using (lockHandle)
    {
        // Critical section - only this instance can access
        await ProcessCriticalOperation();
    }
}
```

**Setup Requirements**:

Before using distributed locks, you must initialize the KV stores:

```csharp
var cnc = new CloopsNatsClient();
await cnc.SetupKVStoresAsync(); // Required before using locks
```

**Basic Lock Usage**:

```csharp
// Acquire a lock with default timeout (1.5 seconds)
var lockHandle = await cnc.AcquireDistributedLockAsync("my-resource-key");
if (lockHandle != null)
{
    await using (lockHandle) // Automatic cleanup when disposed
    {
        // Critical section - only this instance can access the resource
        await ProcessCriticalOperation();
    }
    // Lock is automatically released here
}
else
{
    // Failed to acquire lock - another instance holds it
    Console.WriteLine("Resource is currently locked by another instance");
}
```

**Advanced Lock Configuration**:

```csharp
// Custom timeout and owner identification
var lockHandle = await cnc.AcquireDistributedLockAsync(
    key: "database-migration-lock",
    timeout: TimeSpan.FromSeconds(30),  // Try for 30 seconds
    ownerId: "service-instance-001"     // Identify this instance
);

if (lockHandle != null)
{
    await using (lockHandle)
    {
        // Perform database migration or other critical operation
        await PerformDatabaseMigration();
    }
}
```

**Lock Contention Handling**:

```csharp
// Handle lock contention gracefully
var lockHandle = await cnc.AcquireDistributedLockAsync("shared-resource");
if (lockHandle == null)
{
    // Resource is busy - implement retry logic or alternative path
    await Task.Delay(1000); // Wait before retry
    return; // Or implement exponential backoff
}

await using (lockHandle)
{
    // Exclusive access to shared resource
    await UpdateSharedResource();
}
```

**Multiple Resource Locks**:

```csharp
// Lock different resources independently
var lock1 = await cnc.AcquireDistributedLockAsync("resource-a");
var lock2 = await cnc.AcquireDistributedLockAsync("resource-b");

if (lock1 != null && lock2 != null)
{
    await using (lock1)
    await using (lock2)
    {
        // Both resources are locked
        await ProcessWithMultipleResources();
    }
}
else
{
    // Release any acquired locks
    lock1?.Dispose();
    lock2?.Dispose();
}
```

### 🔧 Queue Groups Implementation

**Purpose**: Load balancing and horizontal scaling for message consumers.

**Implementation Details**:

- **Automatic load balancing**: Messages distributed across queue group members
- **Fault tolerance**: Surviving members continue processing if instances fail
- **No configuration required**: Just specify queue group name in attribute
- **Multiple queue groups**: Different groups can process same subjects independently

**Examples**:

```csharp
// No queue group - all instances receive all messages (broadcast)
[NatsConsumer("notifications.all")]
public async Task<NatsAck> HandleAllNotifications(NatsMsg<Notification> msg, CancellationToken ct)
{
    return NatsAck.Success;
}

// Queue group - load balanced across group members
[NatsConsumer("orders.process", _QueueGroupName: "order-workers")]
public async Task<NatsAck> ProcessOrder(NatsMsg<Order> msg, CancellationToken ct)
{
    return NatsAck.Success;
}
```

### 🔄 Request-Reply Communication

**Purpose**: Synchronous communication pattern for immediate response requirements.

**Characteristics**:

- **Bidirectional**: Send request and await typed response
- **Timeout support**: Configurable timeouts for reliability
- **Type safety**: Both request and response are strongly typed

**Use Cases**:

- Data queries
- Service-to-service API calls
- Validation requests

**Example**:

**Responder using annotation-based handler:**

```csharp
// Responder handler using [NatsConsumer] attribute
[NatsConsumer("service.query")]
public async Task<NatsAck> HandleQuery(NatsMsg<string> msg, CancellationToken ct)
{
    // Process the request
    var result = $"Processed: {msg.Data}";

    // Return NatsAck with reply data - SDK automatically sends reply to requester
    return new NatsAck(true, result);
}
```

**Sending request from NATS CLI:**

```bash
# Send request and await response
nats req service.query "request-data"
```

**Or using SDK programmatically:**

```csharp
var cnc = new CloopsNatsClient();
var response = await cnc.RequestAsync<string, string>("service.query", "request-data");
Console.WriteLine($"Response: {response.Data}");
```

### 📊 Metrics & Observability

**Purpose**: Built-in metrics collection for monitoring message processing performance.

**Key Features**:

- **Automatic metrics**: SDK records message processing duration and status
- **System.Diagnostics.Metrics integration**: Uses standard .NET metrics infrastructure
- **Function-level tracking**: Metrics tagged with handler function name
- **Status tracking**: Success/failure and retryability information

**Available Metrics**:

- **`nats_sub_msg_process_milliseconds`**: Histogram tracking message processing time
  - Labels: `fn` (function name), `status` (success/fail), `retryable` (true/false)
  - Automatically generates count, sum, and bucket metrics for quantiles

**Quick Example**:

```csharp
// Register metrics service
services.AddSingleton<INatsMetricsService, NatsMetricsService>();

// Metrics automatically tracked for all handlers
[NatsConsumer("events.process")]
public async Task<NatsAck> ProcessEvent(NatsMsg<Event> msg, CancellationToken ct)
{
    await ProcessMessage(msg.Data);
    return NatsAck.Success; // Duration and status automatically recorded
}
```

**Metrics Integration**:

The `NatsMetricsService` uses `System.Diagnostics.Metrics`, which integrates with:

- OpenTelemetry exporters
- Application Insights
- Prometheus
- Custom metrics collectors

### 🔑 Token Minting Service

> Only applicable if you are using NATS with decentralized auth.

This is typically used to issue short lived JWT to UI.

**Purpose**: Programmatically mint NATS user credentials (JWT tokens) for dynamic user provisioning.

**Key Features**:

- **Dynamic credential generation**: Create NATS credentials on-demand
- **Fine-grained permissions**: Specify allow/deny lists for publish and subscribe
- **Expiration control**: Set credential expiration times
- **Environment-based configuration**: Secure credential storage via environment variables

**Security Note**: This service requires account signing credentials and should only be used in trusted, secure services.

**Quick Example**:

```csharp
// Register service (uses env vars: NATS_ACCOUNT_SIGNING_SEED, NATS_ACCOUNT_PUBLIC_KEY)
services.AddSingleton<INatsTokenMintingService, NatsTokenMintingService>();

// Mint credentials
var creds = mintingService.MintNatsUserCreds(new NatsCredsRequest
{
    userName = "user-001",
    allowPubs = new List<string> { "events.>" },
    allowSubs = new List<string> { "events.>" },
    expMs = 3600_000 // 1 hour
});
```

**Environment Variables** (see [Environment Variables](./EnvironmentVariables.md)):

- `NATS_ACCOUNT_SIGNING_SEED`: Account signing key seed (highly confidential)
- `NATS_ACCOUNT_PUBLIC_KEY`: Main account public key

### 📋 Subject Builders (External Package)

**Purpose**: Type-safe subject construction (available via external schema packages, e.g. for connection loops, `cloops.nats.schema`). You can build your own package that provides schema.

**Note**: Subject builders are defined in external packages and are **optional**. You can use plain subject strings directly with the SDK.

**Using Subject Builders** (if available from external package):

```csharp
var sb = new CPSubjectBuilder(cnc); // From external schema package
var subject = sb.EventSubjects("cloudpathology_test").P_EffectTriggered;
await subject.Publish(new EffectTriggered { Id = "123" });
```

**Using Plain Subject Strings** (SDK-native approach):

```csharp
var cnc = new CloopsNatsClient();
await cnc.PublishAsync("CP.test.EffectTriggered", new EffectTriggered { Id = "123" });
```

using a schema package will allow you to -

- **Strong typing** for all messages, subjects and their associations. This is provided through `cloops.nats.schema`.
- **Compile-time safety** for event publishing
- **Well-defined patterns** for message governance

These schemas are specific to organization functions and therefore are not included in this SDK. You'd typically want to only allow one schema per subject for reliability. Make sure to version your schemas to be backward compatible.

### 📤 NATS Core Publishing

**Purpose**: Lightweight, fire-and-forget message publishing for high-throughput scenarios.

**Characteristics**:

- **At-most-once delivery**: Messages may be lost if no subscribers are available
- **Low latency**: Minimal overhead for real-time communication
- **Stateless**: No message persistence or durability guarantees

**Use Cases**:

- Real-time notifications
- Telemetry data
- Non-critical updates

**Example**:

```csharp
var cnc = new CloopsNatsClient();
var sb = new CPSubjectBuilder(cnc);
var subject = sb.EventSubjects("cloudpathology_test").P_EffectTriggered;
await subject.Publish(new EffectTriggered { Id = "123" });
```

### 📦 JetStream Publishing

**Purpose**: Durable, reliable message publishing with persistence and delivery guarantees.

**Characteristics**:

- **At-least-once delivery**: Messages are persisted and guaranteed delivery
- **Stream persistence**: Messages stored in streams for replay and durability
- **Acknowledgment support**: Confirm successful message processing
- **ACK/NAK contract**: Implemented. Handlers return `Task<NatsAck>`; processor ACKs or NAKs JetStream messages accordingly.

**Use Cases**:

- Critical business events
- Audit trails
- Data synchronization between services

**Example**:

```csharp
var cnc = new CloopsNatsClient();
var sb = new CPSubjectBuilder(cnc);
var subject = sb.EventSubjects("cloudpathology_test").P_EffectTriggered;
await subject.StreamPublish(new EffectTriggered { Id = "123" }, dedupeId: "unique-123");
```

> Please note: subject builders, subjects and their associated message type bindings are external to this SDK. Please use your own implementations for these.

## 🏛️ Examples Project Architecture

**Purpose**: Demonstrate SDK usage as a separate consumer project.

**Project Structure**:

- **Separate executable project**: `examples/cloops.nats.examples.csproj`
- **SDK consumption**: References SDK as DLL through project reference
- **Command-line interface**: Complete CLI for running all examples
- **Production patterns**: Shows real-world usage scenarios

**Available Examples**:

- `dotnet run pub` - Basic NATS Core publishing
- `dotnet run req` - Request-reply communication
- `dotnet run locking` - distributed lock demo
- `dotnet run minting` - jwt minting demo

**Example Files**:

- **`ConsumerExample.cs`**: Comprehensive consumer examples
- **`NatsConsumerHost.cs`**: Host and DI setup for consumers
- **`TokenMintingExample.cs`**: How to mint a JWT programmatically. Note: only works when you are using NATS decentralized auth.

## 🔧 SDK Components

### Core Classes

**Client & Connectivity**:

- **`Client.cs`**: Main SDK client for NATS operations and connection management
- **`CloopsNatsClient`**: Primary interface for all NATS operations

**Consumer Framework**:

- **`NatsSubscriptionProcessor.cs`**: Handles consumer registration and message routing (both NATS Core and JetStream)
- **`NatsSubscriptionQueue.cs`**: Queue management for consumer groups and batch processing
- **`NatsConsumerAttribute.cs`**: Attribute for marking consumer methods with configuration options

**Serialization & Utilities**:

- **`Util.cs`** / **`BaseNatsUtil`**: Type conversion utilities for NatsMsg processing and JSON serialization
- **`CloopsSerializer.cs`**: Custom serializer implementation with camelCase naming and flexible number handling
- **Method overloading**: Handles both `NatsMsg<byte[]>` and `NatsJSMsg<byte[]>` message types

**Infrastructure Services**:

- **`NatsMetricsService.cs`**: Metrics collection using System.Diagnostics.Metrics
- **`NatsTokenMintingService.cs`**: Programmatic NATS credential generation
- **`KvDistributedLock.cs`**: Distributed locking using NATS Key-Value stores
- **`SubjectMatcher.cs`**: Efficient subject pattern matching for wildcard subscriptions

**Note**: Subject builders, message schemas, and event definitions are provided by external packages (e.g., `cloops.nats.schema`). The SDK works with any strongly-typed message types.

> There is an quick example of schema in examples folder.
