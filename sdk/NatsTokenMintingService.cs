using NATS.Jwt;
using NATS.NKeys;
using CLOOPS.NATS.Messages;

namespace CLOOPS.NATS;


/// <summary>
/// Interface to mint NATS user credentials
/// </summary>
public interface INatsTokenMintingService
{
    /// <summary>
    /// Mint a NATS user credentials
    /// </summary>
    /// <param name="reqPayload">The request payload</param>
    /// <returns>The user credentials</returns>
    /// <exception cref="Exception">Thrown when account signing seed and public key are not set</exception>
    string MintNatsUserCreds(NatsCredsRequest reqPayload);
}

/// <summary>
/// Service to mint NATS user credentials
/// </summary>
public class NatsTokenMintingService : INatsTokenMintingService
{

    // On Nats-box, 
    // run `cd /data/nsc/nkeys/keys/A`
    // run `find . -type f -name "*.nk" -o -name "*.seed"`
    // run `cat <account-signing-public-key>.nk` to get the account signing seed. (remember to pick public key of singing account not main account)
    private readonly string? accountSigningSeed;

    // Run this on nats-box to get the account public key: nsc list keys --account=<account-name> (remember to pick the main account not signing key)
    private readonly string? accountPublicKey;

    /// <summary>
    /// Constructor to initialize the service with account signing seed and public key
    /// </summary>
    /// <param name="accountSigningSeed">The account signing seed</param>
    /// <param name="accountPublicKey">The account public key</param>
    public NatsTokenMintingService(string accountSigningSeed, string accountPublicKey)
    {
        this.accountSigningSeed = accountSigningSeed;
        this.accountPublicKey = accountPublicKey;
    }

    /// <summary>
    /// Constructor to initialize the service from environment variables
    /// </summary>
    public NatsTokenMintingService()
    {
        // assign from env
        this.accountSigningSeed = Environment.GetEnvironmentVariable("NATS_ACCOUNT_SIGNING_SEED");
        this.accountPublicKey = Environment.GetEnvironmentVariable("NATS_ACCOUNT_PUBLIC_KEY");
    }


    /// <inheritdoc/>
    public string MintNatsUserCreds(NatsCredsRequest reqPayload)
    {
        if (accountSigningSeed is null || accountPublicKey is null)
        {
            throw new Exception("Account signing seed and public key are required");
        }

        // Account signing key seed
        var accountSigner = KeyPair.FromSeed(accountSigningSeed);

        // User keypair
        var userKP = KeyPair.CreatePair(PrefixByte.User);
        string userPublic = userKP.GetPublicKey();
        string userSeed = userKP.GetSeed();

        // User claims
        var uc = NatsJwt.NewUserClaims(userPublic);
        // public key of account
        uc.User.IssuerAccount = accountPublicKey; // need main account public key here
        uc.Issuer = accountSigner.GetPublicKey();
        uc.User.Pub.Allow = reqPayload.allowPubs;
        uc.User.Pub.Deny = reqPayload.denyPubs;
        uc.User.Sub.Allow = reqPayload.allowSubs;
        uc.User.Sub.Deny = reqPayload.denySubs;
        uc.Name = reqPayload.userName;
        uc.Expires = DateTimeOffset.UtcNow.AddMilliseconds(reqPayload.expMs);

        // Encode JWT with account signing key
        string userJwt = NatsJwt.EncodeUserClaims(uc, accountSigner);

        // Inline creds string (instead of writing to file)
        string credsInline = NatsJwt.FormatUserConfig(userJwt, userSeed);
        return credsInline;
    }


}