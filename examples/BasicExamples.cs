#if DEBUG
using System.Net;
using CLOOPS.NATS.Messages.CP.Infra;
using NATS.Client.Core;

namespace CLOOPS.NATS.Examples;

public class Program
{
    public static async Task Main(string[] args)
    {
        Console.WriteLine("CLOOPS NATS Examples");
        Console.WriteLine("===================");

        if (args.Length == 0)
        {
            Console.WriteLine("Available examples:");
            Console.WriteLine("  dotnet run pub");
            Console.WriteLine("  dotnet run spub");
            Console.WriteLine("  dotnet run req");
            Console.WriteLine("  dotnet run reply");
            Console.WriteLine("  dotnet run sub");
            Console.WriteLine("  dotnet run subjects");
            Console.WriteLine("  dotnet run batch");
            Console.WriteLine("  dotnet run queuetest");
            Console.WriteLine("  dotnet run queuetestall");
            Console.WriteLine("  dotnet run locking");
            return;
        }

        try
        {
            switch (args[0].ToLower())
            {
                case "pub":
                    await BasicExamples.PublishExample().ConfigureAwait(false);
                    break;
                case "spub":
                    await BasicExamples.StreamPublishExample().ConfigureAwait(false);
                    break;
                case "req":
                    await BasicExamples.RequestExample().ConfigureAwait(false);
                    break;
                case "reply":
                    await BasicExamples.ReplyExample().ConfigureAwait(false);
                    break;
                case "sub":
                    await ConsumerHost.NatsConsumerRunner();
                    break;
                case "subjects":
                    await BasicExamples.SubjectBuilderExample().ConfigureAwait(false);
                    break;
                case "batch":
                    await BasicExamples.BatchProcessingExample().ConfigureAwait(false);
                    break;
                case "queuetest":
                    await BasicExamples.QueueGroupTestExample().ConfigureAwait(false);
                    break;
                case "queuetestall":
                    await BasicExamples.QueueGroupTestAllCases().ConfigureAwait(false);
                    break;
                case "locking":
                    await BasicExamples.LockingTestAllCases().ConfigureAwait(false);
                    break;
                default:
                    Console.WriteLine($"Unknown example: {args[0]}");
                    Console.WriteLine("Available examples:");
                    Console.WriteLine("  dotnet run pub");
                    Console.WriteLine("  dotnet run spub");
                    Console.WriteLine("  dotnet run req");
                    Console.WriteLine("  dotnet run reply");
                    Console.WriteLine("  dotnet run sub");
                    Console.WriteLine("  dotnet run subjects");
                    Console.WriteLine("  dotnet run batch");
                    Console.WriteLine("  dotnet run queuetest");
                    Console.WriteLine("  dotnet run queuetestall");
                    break;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error running example: {ex.Message}");
            Console.WriteLine($"Make sure NATS server is running on dev.nats.cloops.in:4222");
        }
    }
}

internal partial class BasicExamples
{
    internal static async Task PublishExample()
    {
        Console.WriteLine("Running Publish Example...");
        var cnc = new CloopsNatsClient();
        var sb = new SubjectBuilders.CP.CPSubjectBuilder(cnc);
        var subject = sb.EventSubjects("cloudpathology_deesha").P_EffectTriggered;
        EffectTriggered payload = new()
        {
            Id = Guid.NewGuid().ToString(),
            Url = "https://api.example.com/endpoint",
            Method = HttpMethod.Put,
            Body = "{}",
            Headers = new() { Cpt = "bearer-token" },
            StatusCode = HttpStatusCode.Accepted,
            Response = "success",
            SysCreated = DateTime.UtcNow
        };
        await subject.Publish(payload).ConfigureAwait(false);
        Console.WriteLine("Successfully published...");
    }
    internal static async Task StreamPublishExample()
    {
        Console.WriteLine("Running Durable Publish Example...");
        var cnc = new CloopsNatsClient();
        var sb = new SubjectBuilders.CP.CPSubjectBuilder(cnc);
        var subject = sb.EventSubjects("cloudpathology_deesha").P_EffectTriggered;
        await subject.StreamPublish(new()
        {
            Id = Guid.NewGuid().ToString(),
            Url = "https://api.example.com/endpoint",
            Method = HttpMethod.Put,
            Body = "{}",
            Headers = new() { Cpt = "bearer-token" },
            StatusCode = HttpStatusCode.Accepted,
            Response = "success",
            SysCreated = DateTime.UtcNow
        }, dedupeId: "gk", throwOnDuplicate: false);
        Console.WriteLine("Success!");
    }
    internal static async Task RequestExample()
    {
        Console.WriteLine("Running Request Example...");
        var cnc = new CloopsNatsClient();
        var resp = await cnc.RequestAsync<string, string>("test", "hey").ConfigureAwait(false);
        if (resp.HasNoResponders)
        {
            Console.WriteLine("[Error 404]: There are no responders. Please run a replier with:\n nats reply test hi");
        }

    }
    internal static async Task ReplyExample()
    {
        Console.WriteLine("Running Replier...");
        Console.WriteLine("Send request using: nats req test hey");
        var cnc = new CloopsNatsClient();
        await foreach (NatsMsg<string> msg in cnc.SubscribeAsync<string>("test", cancellationToken: default).ConfigureAwait(false))
        {
            Console.WriteLine($"Received request: {msg.Data}");
            await msg.ReplyAsync("hi", cancellationToken: default).ConfigureAwait(false);
        }
    }

    internal static async Task QueueGroupTestExample()
    {
        Console.WriteLine("Running Queue Group Test Example...");
        Console.WriteLine("This will publish messages to test queue group load balancing");

        var cnc = new CloopsNatsClient();
        var sb = new SubjectBuilders.CP.CPSubjectBuilder(cnc);

        Console.WriteLine("Publishing 10 messages to CP.test.EffectTriggered.LoadBalanced");
        Console.WriteLine("If you have multiple consumers with the same queue group, they should share the work");

        for (int i = 0; i < 10; i++)
        {
            EffectTriggered payload = new()
            {
                Id = $"queue-test-{i}",
                Url = $"https://api.example.com/endpoint/{i}",
                Method = HttpMethod.Put,
                Body = $"{{\"message\": \"Queue test {i}\"}}",
                Headers = new() { Cpt = "bearer-token" },
                StatusCode = HttpStatusCode.Accepted,
                Response = "success",
                SysCreated = DateTime.UtcNow
            };

            await cnc.PublishAsync("CP.test.EffectTriggered.LoadBalanced", payload).ConfigureAwait(false);
            Console.WriteLine($"Published message {i + 1}/10");
            await Task.Delay(500).ConfigureAwait(false); // Small delay to see distribution
        }

        Console.WriteLine("All messages published. Check consumers to see load balancing in action.");
    }

    internal static async Task QueueGroupTestAllCases()
    {
        Console.WriteLine("Running Comprehensive Queue Group Test...");
        Console.WriteLine("=========================================");

        var cnc = new CloopsNatsClient();

        Console.WriteLine("Testing all queue group scenarios:");
        Console.WriteLine("1. No queue group (all instances receive)");
        Console.WriteLine("2. Empty string queue group");
        Console.WriteLine("3. Queue group 'workers' (load balanced)");
        Console.WriteLine("4. Queue group 'processors' (separate pool)");
        Console.WriteLine();

        // Test Case 1: No queue group
        Console.WriteLine("Publishing to test.noqueue (no queue group)...");
        for (int i = 0; i < 3; i++)
        {
            await cnc.PublishAsync("test.noqueue", $"No queue message {i + 1}").ConfigureAwait(false);
            await Task.Delay(200).ConfigureAwait(false);
        }

        await Task.Delay(1000).ConfigureAwait(false);

        // Test Case 2: Empty string queue group
        Console.WriteLine("Publishing to test.emptyqueue (empty string queue group)...");
        for (int i = 0; i < 3; i++)
        {
            await cnc.PublishAsync("test.emptyqueue", $"Empty queue message {i + 1}").ConfigureAwait(false);
            await Task.Delay(200).ConfigureAwait(false);
        }

        await Task.Delay(1000).ConfigureAwait(false);

        // Test Case 3 & 4: Queue groups
        Console.WriteLine("Publishing to test.queuegroup (multiple queue groups)...");
        for (int i = 0; i < 6; i++)
        {
            await cnc.PublishAsync("test.queuegroup", $"Queue group message {i + 1}").ConfigureAwait(false);
            await Task.Delay(300).ConfigureAwait(false);
        }

        Console.WriteLine();
        Console.WriteLine("Test complete! Expected behavior:");
        Console.WriteLine("- No queue: ALL consumers should receive ALL messages");
        Console.WriteLine("- Empty queue: ALL consumers should receive ALL messages");
        Console.WriteLine("- Queue groups: Only ONE consumer per group should receive each message");
    }

    internal static async Task SubjectBuilderExample()
    {
        Console.WriteLine("Subject Builder Example");
        Console.WriteLine("======================");
        Console.WriteLine("Demonstrating type-safe subject construction...");

        var cnc = new CloopsNatsClient();
        var sb = new SubjectBuilders.CP.CPSubjectBuilder(cnc);

        // Example 1: Event subjects
        Console.WriteLine("\n1. Event Subject Construction:");
        var eventSubjects = sb.EventSubjects("cloudpathology_test");
        var effectTriggeredSubject = eventSubjects.P_EffectTriggered;

        Console.WriteLine($"   Built subject: {effectTriggeredSubject.SubjectName}");
        Console.WriteLine("   Type safety: Can only publish EffectTriggered events to this subject");

        // Example 2: Publishing to the constructed subject
        Console.WriteLine("\n2. Publishing with Subject Builder:");
        var payload = new EffectTriggered
        {
            Id = Guid.NewGuid().ToString(),
            Url = "https://example.com/subject-builder-test",
            Method = HttpMethod.Get,
            Body = "Subject builder demonstration",
            Headers = new() { Cpt = "subject-builder-token" },
            StatusCode = HttpStatusCode.OK,
            Response = "success",
            SysCreated = DateTime.UtcNow
        };

        await effectTriggeredSubject.Publish(payload).ConfigureAwait(false);
        Console.WriteLine("   ✅ Published using type-safe subject builder");

        // Example 3: JetStream publishing
        Console.WriteLine("\n3. JetStream Publishing with Subject Builder:");
        await effectTriggeredSubject.StreamPublish(payload, dedupeId: $"demo-{payload.Id}").ConfigureAwait(false);
        Console.WriteLine("   ✅ Published to JetStream using subject builder");

        Console.WriteLine("\n📋 Subject Builder Benefits:");
        Console.WriteLine("   • Compile-time type safety");
        Console.WriteLine("   • No runtime subject construction errors");
        Console.WriteLine("   • IntelliSense support");
        Console.WriteLine("   • Enforces correct event-to-subject mapping");
    }

    internal static async Task BatchProcessingExample()
    {
        Console.WriteLine("High-Performance Batch Processing Example");
        Console.WriteLine("========================================");
        Console.WriteLine("Demonstrating batching and parallelism features...");

        var cnc = new CloopsNatsClient();

        Console.WriteLine("\n🚀 Publishing test messages for batch processing...");

        // Publish multiple messages rapidly to test batching
        for (int i = 0; i < 20; i++)
        {
            var payload = new EffectTriggered
            {
                Id = $"batch-test-{i:D3}",
                Url = $"https://batch.example.com/item/{i}",
                Method = HttpMethod.Post,
                Body = $"{{\"batchItem\": {i}, \"timestamp\": \"{DateTime.UtcNow:O}\"}}",
                Headers = new() { Cpt = "batch-test-token" },
                StatusCode = HttpStatusCode.Accepted,
                Response = $"batch-item-{i}",
                SysCreated = DateTime.UtcNow
            };

            await cnc.PublishAsync("test.batch.processing", payload).ConfigureAwait(false);

            if (i % 5 == 0)
            {
                Console.WriteLine($"   Published batch {i + 1}/20");
            }
        }

        Console.WriteLine("\n📊 Batch Processing Features Demonstrated:");
        Console.WriteLine("   • High message throughput");
        Console.WriteLine("   • Parallel processing (maxDOP)");
        Console.WriteLine("   • Batch formation with timeouts");
        Console.WriteLine("   • Channel capacity management");
        Console.WriteLine("\n💡 To see batching in action, run a consumer with:");
        Console.WriteLine("   [NatsConsumer(\"test.batch.processing\", _useBatching: true, _maxDOP: 10)]");

        await Task.Delay(1000).ConfigureAwait(false);
        Console.WriteLine("\n✅ Batch processing test completed!");
    }

    internal static async Task LockingTestAllCases()
    {
        Console.WriteLine("Locking example");
        Console.WriteLine("========================================");
        Console.WriteLine("Demonstrate locks");

        var cnc = new CloopsNatsClient();
        await cnc.SetupKVStoresAsync();

        Console.WriteLine("Getting lock....");
        var handle1 = await cnc.AcquireDistributedLockAsync("l1", TimeSpan.FromSeconds(4000), "first");
        if (handle1 is null)
        {
            Console.WriteLine("test failed. couldn't get lock.  ❌");
            return;
        }
        await using (handle1)
        {
            Console.WriteLine("Got the lock. awesome ✅");
            Console.WriteLine("Trying as someone else to get the lock");
            var handle2 = await cnc.AcquireDistributedLockAsync("l1", ownerId: "second");
            if (handle2 is not null)
            {
                Console.WriteLine("Got the lock. test failed. I shouldn't get gotten the lock. ❌");
                return;
            }
            Console.WriteLine("couldn't get the lock. awesome ✅");
            Console.WriteLine("Let's see if we can get a lock for another key");
            var handle3 = await cnc.AcquireDistributedLockAsync("l2", ownerId: "third");
            if (handle3 is null)
            {
                Console.WriteLine("test failed. couldn't get lock.  ❌");
                return;
            }
            await using (handle3)
            {
                Console.WriteLine("got lock for a different resource. awesome ✅");
            }
        }

        Console.WriteLine("Released original lock, trying to get it again");
        Console.WriteLine("Trying again to get lock");

        var handle4 = await cnc.AcquireDistributedLockAsync("l1", ownerId: "forth");
        if (handle4 is null)
        {
            Console.WriteLine("test failed. couldn't get lock.  ❌");
            return;
        }
        await using (handle4)
        {
            Console.WriteLine("got the lock awesome ✅");
        }

        Console.WriteLine("All tests ran successfully");


    }
}
#endif