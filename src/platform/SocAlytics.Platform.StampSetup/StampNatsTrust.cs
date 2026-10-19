using NATS.Jwt;
using NATS.NKeys;

namespace SocAlytics.Platform.StampSetup;

/// <summary>A new NATS operator, stamp account, and system account for one stamp, with the server configuration that trusts them.</summary>
public sealed class StampNatsTrust
{
    private readonly KeyPair _operator = KeyPair.CreatePair(PrefixByte.Operator);
    private readonly KeyPair _account = KeyPair.CreatePair(PrefixByte.Account);
    private readonly KeyPair _system = KeyPair.CreatePair(PrefixByte.Account);

    public string OperatorSeed => _operator.GetSeed();

    public string AccountSeed => _account.GetSeed();

    public string SystemAccountSeed => _system.GetSeed();

    public string BuildServerConfig()
    {
        var operatorClaims = NatsJwt.NewOperatorClaims(_operator.GetPublicKey());
        operatorClaims.Name = "socalytics";
        operatorClaims.Operator.SystemAccount = _system.GetPublicKey();

        var accountClaims = NatsJwt.NewAccountClaims(_account.GetPublicKey());
        accountClaims.Name = "SOCALYTICS";
        var limits = accountClaims.Account.Limits;
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

        var systemClaims = NatsJwt.NewAccountClaims(_system.GetPublicKey());
        systemClaims.Name = "SYS";

        return $$"""
            port: 4222
            http_port: 8222
            server_name: socalytics-stamp
            operator: {{NatsJwt.EncodeOperatorClaims(operatorClaims, _operator)}}
            system_account: {{_system.GetPublicKey()}}
            resolver: {
              type: full
              dir: '/data/resolver'
              allow_delete: false
              interval: "2m"
            }
            resolver_preload: {
              {{_account.GetPublicKey()}}: {{NatsJwt.EncodeAccountClaims(accountClaims, _operator)}}
              {{_system.GetPublicKey()}}: {{NatsJwt.EncodeAccountClaims(systemClaims, _operator)}}
            }
            jetstream: {
              store_dir: '/data/jetstream'
            }

            """;
    }
}
