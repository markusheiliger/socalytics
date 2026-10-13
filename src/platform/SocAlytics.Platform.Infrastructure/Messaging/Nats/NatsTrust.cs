using Microsoft.Extensions.Options;
using NATS.Jwt;
using NATS.Jwt.Models;
using NATS.NKeys;

namespace SocAlytics.Platform.Infrastructure.Messaging.Nats;

/// <summary>Derives the operator, stamp account, and system account from their seeds and mints NATS JWTs.</summary>
internal sealed class NatsTrust
{
    public const string StampAccountName = "SOCALYTICS";
    public const string SystemAccountName = "SYS";
    public const string OperatorName = "socalytics";

    private readonly KeyPair? _operator;
    private readonly KeyPair? _account;
    private readonly KeyPair? _system;

    public NatsTrust(IOptions<NatsAuthenticationOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        Mode = options.Value.Mode;
        if (Mode != NatsAuthenticationMode.Jwt)
        {
            return;
        }

        _operator = ReadSeed(options.Value.OperatorSeed, PrefixByte.Operator, nameof(NatsAuthenticationOptions.OperatorSeed));
        _account = ReadSeed(options.Value.AccountSeed, PrefixByte.Account, nameof(NatsAuthenticationOptions.AccountSeed));
        _system = ReadSeed(options.Value.SystemAccountSeed, PrefixByte.Account, nameof(NatsAuthenticationOptions.SystemAccountSeed));
    }

    public NatsAuthenticationMode Mode { get; }

    public bool IsJwt => Mode == NatsAuthenticationMode.Jwt;

    public string OperatorPublicKey => Require(_operator).GetPublicKey();

    public string StampAccountPublicKey => Require(_account).GetPublicKey();

    public string SystemAccountPublicKey => Require(_system).GetPublicKey();

    /// <summary>Generates throwaway seeds for local composition and tests.</summary>
    public static (string OperatorSeed, string AccountSeed, string SystemAccountSeed) GenerateSeeds() =>
        (
            KeyPair.CreatePair(PrefixByte.Operator).GetSeed(),
            KeyPair.CreatePair(PrefixByte.Account).GetSeed(),
            KeyPair.CreatePair(PrefixByte.Account).GetSeed());

    public string BuildOperatorJwt()
    {
        var op = Require(_operator);
        var claims = NatsJwt.NewOperatorClaims(op.GetPublicKey());
        claims.Name = OperatorName;
        claims.Operator.SystemAccount = SystemAccountPublicKey;
        return NatsJwt.EncodeOperatorClaims(claims, op);
    }

    public string BuildStampAccountJwt()
    {
        var claims = NatsJwt.NewAccountClaims(StampAccountPublicKey);
        claims.Name = StampAccountName;
        var limits = claims.Account.Limits;
        limits.Subs = -1;
        limits.Data = -1;
        limits.Payload = -1;
        limits.Imports = -1;
        limits.Exports = -1;
        limits.WildcardExports = true;
        limits.Conn = -1;
        limits.LeafNodeConn = -1;
        limits.MemoryStorage = -1;
        limits.DiskStorage = -1;
        limits.Streams = -1;
        limits.Consumer = -1;
        limits.MaxAckPending = -1;
        limits.MemoryMaxStreamBytes = -1;
        limits.DiskMaxStreamBytes = -1;
        return NatsJwt.EncodeAccountClaims(claims, Require(_operator));
    }

    public string BuildSystemAccountJwt()
    {
        var claims = NatsJwt.NewAccountClaims(SystemAccountPublicKey);
        claims.Name = SystemAccountName;
        return NatsJwt.EncodeAccountClaims(claims, Require(_operator));
    }

    /// <summary>Mints a user of the stamp account with a fresh key pair.</summary>
    public (string Jwt, string Seed) CreateUser(
        string name,
        IReadOnlyCollection<string> publishAllow,
        IReadOnlyCollection<string> subscribeAllow,
        DateTimeOffset? expires = null)
    {
        var user = KeyPair.CreatePair(PrefixByte.User);
        return (CreateUserJwt(user.GetPublicKey(), name, publishAllow, subscribeAllow, expires), user.GetSeed());
    }

    /// <summary>Mints a user JWT of the stamp account for a public key whose seed the platform never sees.</summary>
    public string CreateUserJwt(
        string userPublicKey,
        string name,
        IReadOnlyCollection<string> publishAllow,
        IReadOnlyCollection<string> subscribeAllow,
        DateTimeOffset? expires = null)
    {
        var account = Require(_account);
        var claims = NatsJwt.NewUserClaims(userPublicKey);
        claims.Name = name;
        claims.Expires = expires;
        claims.User.Pub = new NatsPermission { Allow = [.. publishAllow] };
        claims.User.Sub = new NatsPermission { Allow = [.. subscribeAllow] };
        claims.User.Subs = -1;
        claims.User.Data = -1;
        claims.User.Payload = -1;
        return NatsJwt.EncodeUserClaims(claims, account);
    }

    /// <summary>Mints the platform's own user: stream and consumer administration, work and event publishing, and consuming.</summary>
    public (string Jwt, string Seed) CreatePlatformUser() =>
        CreateUser(
            "socalytics-platform",
            ["$JS.API.>", "$JS.ACK.>", "analysis.>", "matches.>"],
            ["_INBOX.>"]);

    private static KeyPair ReadSeed(string? seed, PrefixByte expected, string name)
    {
        if (string.IsNullOrWhiteSpace(seed))
        {
            throw new InvalidOperationException($"Nats:Authentication:{name} is required when Nats:Authentication:Mode is Jwt.");
        }

        KeyPair pair;
        try
        {
            pair = KeyPair.FromSeed(seed);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Nats:Authentication:{name} is not a valid NATS seed.", ex);
        }

        if (pair.Prefix != expected)
        {
            throw new InvalidOperationException($"Nats:Authentication:{name} is not a {expected} seed.");
        }

        return pair;
    }

    private static KeyPair Require(KeyPair? pair) =>
        pair ?? throw new InvalidOperationException("NATS JWT authentication is not enabled.");
}
