# CLOOPS NATS SDK - Current Functionality

This document outlines the currently available features and capabilities of the CLOOPS NATS SDK. The SDK is designed to provide type-safe, reliable messaging patterns for distributed microservices.

## 🏗️ Architecture Overview

The SDK follows a layered architecture that enforces type safety and provides clear separation of concerns:

- **Messages Layer**: Strongly-typed message schemas with compile-time validation
- **Subjects Layer**: Type-safe subject builders with capability-based prefixes
- **Transport Layer**: Abstraction over NATS Core and JetStream protocols
- **Consumer Layer**: Attribute-driven message consumption with automatic routing
- **Client Layer**: High-level API for publishing and consuming messages

## ✅ Available Features

### 📋 Subject Builders

**Purpose**: Provide a statically-typed, zero-ambiguity way to construct NATS subjects.

**Key Benefits**:

- **Compile-time safety**: No runtime errors from malformed subjects
- **Capability enforcement**: Subject prefixes clearly indicate intended usage patterns
- **Zero guesswork**: IntelliSense and type system guide correct usage

**Available Subject Types**:

- `P_Subject`: Publish-only subjects for one-way message broadcasting
- `R_Subject`: Request-reply subjects for synchronous communication patterns

**Example Usage**:

```csharp
// Type-safe subject construction
var subject = CPSubjectBuilder.GetEffectTriggeredSubject();
// Compiler ensures you can only publish EffectTriggered messages to this subject
```

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

### 📥 Message Subscription & Consumption

- **Attribute-driven**: Use `[NatsConsumer]` to mark methods as message handlers
- **Automatic routing**: SDK automatically routes messages to appropriate handlers
- **Type safety**: Strongly-typed message handling with compile-time validation
- **Queue groups**: Built-in load balancing across multiple consumer instances
- **Dual protocol support**: Seamless handling of both NATS Core and JetStream messages
- **Explicit ACK/NAK contract**: **All handlers** must return `Task<NatsAck>`. JetStream uses the result to `Ack` on `Success` or `Nak` on `Fail`; Core ignores it.
- **Handler signature enforcement**: The SDK validates at startup that handlers return `Task<NatsAck>`, preventing misconfiguration.
- **Automatic ACK/NAK (JetStream)**: After handler execution, the processor calls `AckAsync` or `NakAsync` based on the handler’s result, guaranteeing at-least-once delivery.
- **Exception safety**: If a handler throws, the processor logs the error and issues a NAK (JetStream), enabling retry/DLQ per configuration.

**Consumer Registration (Updated for JetStream ACK/NAK):**

```csharp
// JetStream consumer with explicit ACK/NAK
[NatsConsumer("CP.*.EffectTriggered.Durable", _durable: true, _consumerId: "effect-durable")]
public async Task<NatsAck> ProcessDurableEffect(NatsMsg<EffectTriggered> msg, CancellationToken ct)
{
    try
    {
        // Business logic here
        await ProcessCriticalEffect(msg.Data);
        return NatsAck.Success; // Message will be ACKed
    }
    catch (Exception ex)
    {
        // Log and trigger NAK for retry
        Console.WriteLine($"Error: {ex.Message}");
        return NatsAck.Fail; // Message will be NAKed
    }
}
```

**Queue Groups for Load Balancing (Unchanged):**

```csharp
[NatsConsumer("CP.*.EffectTriggered.LoadBalanced", _queueGroupName: "effect-processors")]
public async Task<NatsAck> HandleLoadBalancedEffect(NatsMsg<EffectTriggered> msg, CancellationToken ct = default)
{
    // This consumer will share load with other "effect-processors" queue group members
    await ProcessEffectInLoadBalancedGroup(msg.Data);
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

**Durable Consumer Registration (Updated for ACK/NAK):**

```csharp
[NatsConsumer("CP.*.EffectTriggered.Durable", _durable: true, _consumerId: "effect-durable")]
public async Task<NatsAck> ProcessDurableEffect(NatsMsg<EffectTriggered> msg, CancellationToken ct)
{
    try
    {
        await ProcessCriticalEffect(msg.Data);
        return NatsAck.Success; // SDK will Ack
    }
    catch (Exception ex)
    {
        Console.WriteLine(ex);
        return NatsAck.Fail;    // SDK will Nak (JetStream retries per config)
    }
}
```

### ⚡ High-Performance Batch Processing

**Purpose**: Optimized message processing for high-throughput scenarios.

**Key Features**:

- **Configurable parallelism**: Control concurrent processing with `maxDOP`
- **Batch formation**: Group messages for efficient processing
- **Timeout control**: Balance latency vs. throughput with `batchTimeoutMs`
- **Resource management**: Prevent memory overflow with `channelCapacity`

**Batch Processing Configuration**:

```csharp
[NatsConsumer("analytics.events",
    _maxDOP: 100,           // 100 parallel processors
    _useBatching: true,     // Enable batch processing
    _batchTimeoutMs: 50,    // 50ms batch window
    _channelCapacity: 10000)] // Internal queue size
public async Task<NatsAck> ProcessAnalytics(NatsMsg<AnalyticsEvent> msg, CancellationToken ct = default)
{
    // High-throughput optimized processing
    await analyticsService.ProcessEvent(msg.Data);
    return NatsAck.Success;
}
```

### 🔒 Distributed Locking

**Purpose**: Provide distributed locking capabilities using NATS Key-Value stores for coordination across multiple service instances.

**Key Features**:

- **Distributed coordination**: Lock resources across multiple service instances
- **Automatic cleanup**: Locks are automatically released when the handle is disposed
- **Timeout support**: Configurable timeout for lock acquisition attempts
- **Owner identification**: Track which instance holds the lock
- **Resource isolation**: Different keys provide independent locks

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

**Queue Group Scenarios**:

```csharp
// No queue group - all instances receive all messages
[NatsConsumer("notifications.all")]
public async Task<NatsAck> HandleAllNotifications(NatsMsg<Notification> msg, CancellationToken ct)
{
    await Task.CompletedTask;
    return NatsAck.Success;
}

// Queue group - load balanced across group members
[NatsConsumer("orders.process", _queueGroupName: "order-workers")]
public async Task<NatsAck> ProcessOrder(NatsMsg<Order> msg, CancellationToken ct)
{
    await Task.CompletedTask;
    return NatsAck.Success;
}

```

## 🏛️ Examples Project Architecture

**Purpose**: Demonstrate SDK usage as a separate consumer project.

**Project Structure**:

- **Separate executable project**: `examples/cloops.nats.examples.csproj`
- **SDK consumption**: References SDK as DLL through project reference
- **Command-line interface**: Complete CLI for running all examples
- **Production patterns**: Shows real-world usage scenarios

**Available Examples**:

- `dotnet run pub` - Basic NATS Core publishing
- `dotnet run spub` - JetStream publishing with persistence
- `dotnet run req` - Request-reply communication
- `dotnet run reply` - Reply handler setup
- `dotnet run sub` - Consumer examples
- `dotnet run subjects` - Subject builder demonstrations
- `dotnet run batch` - High-performance batch processing
- `dotnet run queuetest` - Queue group load balancing demo

**Example Files**:

- **`BasicExamples.cs`**: Command-line interface demonstrating all SDK features
- **`ConsumerExample.cs`**: Comprehensive consumer examples with hosting integration
- **`JetStreamDurableConsumer.cs`**: JetStream durable consumer patterns
- **`QueueGroupTestConsumer.cs`**: Complete queue group testing scenarios

## 🚧 Work In Progress

### Message Acknowledgment & Retry

- **Retry mechanisms**: Retries are handled by JetStream for NAKed messages per consumer/stream configuration.
- **Exponential backoff**: **Not implemented in SDK** (configure via JetStream consumer options).
- **Dead letter queues (DLQ)**: **Not implemented in SDK** (configure DLQ/move-to-stream in JetStream).

### Advanced Serialization

- **Custom serializers**: Support for serialization beyond JSON
- **Binary protocols**: Protobuf, MessagePack, Avro support
- **Schema evolution**: Backward/forward compatibility handling

### Observability & Metrics

- **Performance metrics**: Message throughput, latency, error rates
- **Health checks**: Consumer health and availability monitoring
- **Distributed tracing**: OpenTelemetry integration
- **Logging integration**: Structured logging with correlation IDs

### Stream Management

- **Automatic stream creation**: Dynamic JetStream stream provisioning
- **Stream configuration**: Programmatic stream setup and management
- **Retention policies**: Configurable message retention strategies

## 🔧 SDK Components

### Core Classes

**Client & Connectivity**:

- **`Client.cs`**: Main SDK client for NATS operations and connection management
- **`CloopsNatsClient`**: Primary interface for all NATS operations

**Consumer Framework**:

- **`NatsSubscriptionProcessor.cs`**: Handles consumer registration and message routing (both NATS Core and JetStream)
- **`NatsSubscriptionQueue.cs`**: Queue management for consumer groups and batch processing
- **`NatsConsumerAttribute.cs`**: Attribute for marking consumer methods with configuration options

**Type System & Utilities**:

- **`Util.cs`**: Type conversion utilities for NatsMsg processing and JSON serialization
- **Method overloading**: Handles both `NatsMsg<byte[]>` and `NatsJSMsg<byte[]>` message types
- **JSON serialization**: Configured with camelCase naming and flexible number handling

### Subject Builders

**Core Subject Building**:

- **`CPSubjectBuilder.cs`**: Subject builder for Connection Loops domains
- **`CPEventsSubjectBuilder.cs`**: Specialized builder for CP events
- **`P_Subject.cs`**: Publish-only subject definitions
- **`R_Subject.cs`**: Request-reply subject definitions

### Event Schemas

**Event Definitions**:

- **Location**: `/Events/` directory hierarchy
- **Organization**: Organized by domain (e.g., `CP/Infra/`)
- **Type safety**: Strongly-typed event classes with validation
- **`EffectTriggered.cs`**: Primary event type for demonstrations

### Examples Project

**Production Examples**:

- **`examples/BasicExamples.cs`**: Command-line interface demonstrating all SDK features
- **`examples/ConsumerExample.cs`**: Comprehensive consumer examples with DI integration
- **`examples/JetStreamDurableConsumer.cs`**: JetStream durable consumer patterns
- **`examples/QueueGroupTestConsumer.cs`**: Queue group testing and validation scenarios

## 📊 Example Workflows

### Publishing an Event

**Complete Publishing Workflow**:

1. Use subject builder to get type-safe subject
2. Create strongly-typed event object
3. Choose publishing method (NATS Core vs JetStream)
4. SDK enforces type compatibility at compile time

```csharp
// Complete publishing example
var cnc = new CloopsNatsClient();
var sb = new CPSubjectBuilder(cnc);
var subject = sb.EventSubjects("cloudpathology_test").P_EffectTriggered;

EffectTriggered payload = new()
{
    Id = Guid.NewGuid().ToString(),
    Url = "https://example.com/effect",
    Method = HttpMethod.Post,
    Body = "{\"test\": \"data\"}",
    Headers = new() { Cpt = "auth-token" },
    StatusCode = HttpStatusCode.OK,
    Response = "success",
    SysCreated = DateTime.UtcNow
};

// NATS Core publishing
await subject.Publish(payload).ConfigureAwait(false);

// JetStream publishing with deduplication
await subject.StreamPublish(payload, dedupeId: $"effect-{payload.Id}").ConfigureAwait(false);
```

### Setting Up Message Consumers

**Complete Consumer Setup Workflow**:

1. Create consumer class with `[NatsConsumer]` attributes
2. Register consumers with dependency injection
3. Start subscription service
4. SDK automatically routes messages to handlers

```csharp
// Consumer class
public class EventProcessor
{
    [NatsConsumer("CP.*.EffectTriggered")]
    public async Task<NatsAck> ProcessEffect(NatsMsg<EffectTriggered> msg, CancellationToken ct)
    {
        Console.WriteLine($"Processing effect: {msg.Data.Id}");
        await Task.CompletedTask;
        return NatsAck.Success; // Core ignores, JetStream would Ack/Nak if durable
    }

    [NatsConsumer("CP.*.EffectTriggered.Bulk", _queueGroupName: "bulk-processors")]
    public async Task<NatsAck> ProcessBulkEffect(NatsMsg<EffectTriggered> msg, CancellationToken ct)
    {
        await ProcessInBulk(msg.Data);
        return NatsAck.Success;
    }

    // If you want to keep a durable example here, align it too:
    [NatsConsumer("CP.*.EffectTriggered.Durable", _durable: true, _consumerId: "effect-durable")]
    public async Task<NatsAck> ProcessDurableEffect(NatsMsg<EffectTriggered> msg, CancellationToken ct)
    {
        try
        {
            await ProcessCriticalEffect(msg.Data);
            return NatsAck.Success; // SDK will Ack
        }
        catch
        {
            return NatsAck.Fail;    // SDK will Nak (JetStream retries per config)
        }
    }
}

// Consumer registration and startup
var host = Host.CreateDefaultBuilder()
    .ConfigureServices(services =>
    {
        services.AddSingleton<EventProcessor>();
        services.AddSingleton<QueueGroupTestConsumer>();
        services.AddSingleton<JetStreamDurableConsumer>();
        services.AddNatsSubscriptionProcessor();
    })
    .Build();

await host.RunAsync();
```

### Request-Reply Pattern

**Complete Request-Reply Workflow**:

1. Set up reply handler for incoming requests
2. Use typed request/response objects
3. Send requests with timeout handling
4. Handle response or timeout scenarios

```csharp
// Reply handler setup
await foreach (NatsMsg<string> msg in cnc.SubscribeAsync<string>("test.requests"))
{
    var response = $"Processed: {msg.Data}";
    await cnc.ReplyAsync(msg, response).ConfigureAwait(false);
}

// Request sender with error handling
try
{
    var response = await cnc.RequestAsync<string, string>("test.requests", "request-data").ConfigureAwait(false);
    Console.WriteLine($"Received response: {response}");
}
catch (TimeoutException)
{
    Console.WriteLine("Request timed out - no responders available");
}
```

## 🎯 Upcoming Features

**Planned Enhancements**:

- **Enhanced subscription management**: Advanced subscription lifecycle control
- **Automatic retry mechanisms**: Built-in retry policies and dead letter queues
- **Metrics and observability integration**: OpenTelemetry and monitoring support
- **Additional subject builders**: Support for new domains and event types
- **Stream management utilities**: Automated JetStream stream provisioning
- **Performance optimizations**: Further throughput and latency improvements

**Not Currently Planned**:

- **Message transformation**: In-flight message modification or routing
- **Complex event processing**: Event correlation and stream processing
- **Multi-tenancy**: Built-in tenant isolation and routing
- **Schema registry**: Centralized schema management and evolution
