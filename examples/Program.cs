#if DEBUG
using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Net;
using CLOOPS.NATS.Extensions;
using SequentialGuid;

namespace CLOOPS.NATS.Examples;

public class Program
{
    static NatsCoreConsumerHost host = new NatsCoreConsumerHost();
    public static async Task Main(string[] args)
    {
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        await host.StartAsync().ConfigureAwait(false);

        Console.WriteLine("CLOOPS NATS Examples");
        Console.WriteLine("======================================");
        Console.WriteLine("Starting consumers...");
        // sleep to give nats lifecycle to setup consumers
        await Task.Delay(1000).ConfigureAwait(false);
        Console.WriteLine("======================================");

        if (args.Length == 0)
        {
            Console.WriteLine("Available examples:");
            Console.WriteLine("  dotnet run pub - publish to nats core");
            Console.WriteLine("  dotnet run req - Send a request");
            Console.WriteLine("  dotnet run locking - Distributed locking");
            Console.WriteLine("  dotnet run minting - Mint a new token");

            Console.WriteLine("\n=========\n");
            Console.WriteLine("Press Ctrl+C to exit.");
            await host.WaitForShutdownAsync(cts.Token).ConfigureAwait(false);
            await host.StopAsync().ConfigureAwait(false);
            return;
        }

        try
        {
            switch (args[0].ToLower())
            {
                case "pub":
                    await PublishExample().ConfigureAwait(false);
                    break;
                case "req":
                    await RequestExample().ConfigureAwait(false);
                    break;
                case "locking":
                    await LockingTestAllCases().ConfigureAwait(false);
                    break;
                case "minting":
                    TokenMintingExample.RunTokenMintingExample();
                    break;
                default:
                    Console.WriteLine($"Unknown example: {args[0]}");
                    break;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error running example: {ex.Message}");
            Console.WriteLine($"Make sure NATS server is running on dev.nats.cloops.in:4222");
        }

        Console.WriteLine();
        Console.WriteLine("Consumers are running. Press Ctrl+C to exit.");
        await host.WaitForShutdownAsync(cts.Token).ConfigureAwait(false);
        await host.StopAsync().ConfigureAwait(false);
    }


    internal static async Task PublishExample()
    {
        Console.WriteLine("Publish Example");
        Console.WriteLine("======================");
        Console.WriteLine("Demonstrating type-safe subject construction...");

        var person = new Person
        {
            Id = SequentialGuidGenerator.Instance.NewGuid().ToString(),
            Name = "Gaurav Kalele",
            Age = 31,
            Addr = "Santa Clara, CA"
        };

        var personSaveSubject = host.cnc.Subjects().Example().P_SavePerson(person.Id);
        var personUpdateSubject = host.cnc.Subjects().Example().S_UpdatePerson(person.Id);

        Console.WriteLine($"   Built subject: {personSaveSubject.SubjectName}");
        Console.WriteLine("   Type safety: Can only publish Person events to this subject");

        // Example 1: Publishing to the constructed subject
        Console.WriteLine("\nPublishing with Subject Builder:");
        await personSaveSubject.Publish(person);

        Console.WriteLine("   ✅ Published using type-safe subject builder");

        // Example 2: JetStream publishing
        Console.WriteLine("\nJetStream Publishing to update person");
        await personUpdateSubject.StreamPublish(person, true, person.Id);
        Console.WriteLine("   ✅ Published to JetStream using subject builder");

        Console.WriteLine("\n📋 Subject Builder Benefits:");
        Console.WriteLine("   • Compile-time type safety");
        Console.WriteLine("   • No runtime payload parsing errors on consumer side");
        Console.WriteLine("   • IntelliSense support");
        Console.WriteLine("   • Enforces correct event-to-subject mapping");

        Console.WriteLine("✅ Test done, if you are seeing received request to save/update person for two times, then it is working fine");
    }


    internal static async Task RequestExample()
    {
        Console.WriteLine("Running Request Example...");
        var person = new Person
        {
            Id = SequentialGuidGenerator.Instance.NewGuid().ToString(),
            Name = "Gaurav",
            Age = 31,
            Addr = "Santa Clara"
        };
        var sb = new SubjectBuilders.ExampleSubjectBuilder(host.cnc);
        var resp = await sb.echo().Request(person);
        Console.WriteLine($"Received: {resp.Data}");
        Console.WriteLine("If you saw the same person back then it worked!");
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