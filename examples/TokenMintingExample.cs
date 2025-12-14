#if DEBUG
using CLOOPS.NATS;
using CLOOPS.NATS.Messages;

namespace CLOOPS.NATS.Examples;

/// <summary>
/// Example demonstrating NATS token minting service
/// This service allows you to programmatically create NATS user credentials
/// </summary>
public class TokenMintingExample
{
    public static async Task RunTokenMintingExample()
    {
        Console.WriteLine("Token Minting Example");
        Console.WriteLine("=====================");
        Console.WriteLine("Demonstrating programmatic NATS credential generation");
        Console.WriteLine();

        // Check for environment variables
        var signingSeed = Environment.GetEnvironmentVariable("NATS_ACCOUNT_SIGNING_SEED");
        var accountKey = Environment.GetEnvironmentVariable("NATS_ACCOUNT_PUBLIC_KEY");

        if (string.IsNullOrEmpty(signingSeed) || string.IsNullOrEmpty(accountKey))
        {
            Console.WriteLine("⚠️  Warning: NATS_ACCOUNT_SIGNING_SEED or NATS_ACCOUNT_PUBLIC_KEY not set");
            Console.WriteLine();
            Console.WriteLine("To use token minting, set these environment variables:");
            Console.WriteLine("  - NATS_ACCOUNT_SIGNING_SEED: Account signing key seed");
            Console.WriteLine("  - NATS_ACCOUNT_PUBLIC_KEY: Main account public key");
            Console.WriteLine();
            Console.WriteLine("This example will demonstrate the API but won't create real credentials.");
            Console.WriteLine();
        }

        // Create minting service (uses environment variables if set)
        INatsTokenMintingService? mintingService = null;
        try
        {
            if (!string.IsNullOrEmpty(signingSeed) && !string.IsNullOrEmpty(accountKey))
            {
                mintingService = new NatsTokenMintingService(signingSeed, accountKey);
                Console.WriteLine("✅ Minting service initialized with provided credentials");
            }
            else
            {
                mintingService = new NatsTokenMintingService();
                Console.WriteLine("✅ Minting service initialized (checking environment variables)");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Error initializing minting service: {ex.Message}");
            Console.WriteLine();
            Console.WriteLine("This example requires valid NATS account credentials.");
            Console.WriteLine("See EnvironmentVariables.md for details on how to obtain them.");
            return;
        }

        Console.WriteLine();
        Console.WriteLine("Example: Minting credentials for a new user");
        Console.WriteLine("-------------------------------------------");

        try
        {
            var request = new NatsCredsRequest
            {
                userName = "example-user-001",
                allowPubs = new List<string> { "events.>", "commands.publish.>" },
                allowSubs = new List<string> { "events.>", "commands.subscribe.>" },
                denyPubs = new List<string> { "admin.>" },
                denySubs = new List<string> { "admin.>" },
                expMs = 3600_000 // 1 hour expiration
            };

            Console.WriteLine($"Request details:");
            Console.WriteLine($"  User: {request.userName}");
            Console.WriteLine($"  Allow Publish: {string.Join(", ", request.allowPubs)}");
            Console.WriteLine($"  Allow Subscribe: {string.Join(", ", request.allowSubs)}");
            Console.WriteLine($"  Deny Publish: {string.Join(", ", request.denyPubs)}");
            Console.WriteLine($"  Deny Subscribe: {string.Join(", ", request.denySubs)}");
            Console.WriteLine($"  Expires: {request.expMs / 1000} seconds ({request.expMs / 3600_000} hours)");
            Console.WriteLine();

            var credentials = mintingService.MintNatsUserCreds(request);

            Console.WriteLine("✅ Credentials minted successfully!");
            Console.WriteLine();
            Console.WriteLine("Generated credentials (first 100 chars):");
            Console.WriteLine(credentials.Substring(0, Math.Min(100, credentials.Length)) + "...");
            Console.WriteLine();
            Console.WriteLine("💡 Usage:");
            Console.WriteLine("   1. Save these credentials to a file (e.g., user.creds)");
            Console.WriteLine("   2. Use with NATS clients: nats --creds user.creds");
            Console.WriteLine("   3. Or pass directly to CloopsNatsClient constructor");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Error minting credentials: {ex.Message}");
            Console.WriteLine();
            Console.WriteLine("Common issues:");
            Console.WriteLine("  - Invalid account signing seed");
            Console.WriteLine("  - Invalid account public key");
            Console.WriteLine("  - Missing or incorrect environment variables");
        }

        Console.WriteLine();
        Console.WriteLine("Example: Short-lived credentials (5 minutes)");
        Console.WriteLine("--------------------------------------------");

        try
        {
            var shortLivedRequest = new NatsCredsRequest
            {
                userName = "temporary-user",
                allowPubs = new List<string> { "temp.>" },
                allowSubs = new List<string> { "temp.>" },
                expMs = 300_000 // 5 minutes
            };

            var shortCredentials = mintingService.MintNatsUserCreds(shortLivedRequest);
            Console.WriteLine("✅ Short-lived credentials created successfully");
            Console.WriteLine($"   Expires in: {shortLivedRequest.expMs / 1000} seconds");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Error: {ex.Message}");
        }

        Console.WriteLine();
        Console.WriteLine("🔒 Security Notes:");
        Console.WriteLine("   • Account signing credentials are highly sensitive");
        Console.WriteLine("   • Only use minting service in trusted, secure environments");
        Console.WriteLine("   • Store credentials securely (environment variables, secrets manager)");
        Console.WriteLine("   • Never commit credentials to version control");
        Console.WriteLine();
    }
}
#endif
