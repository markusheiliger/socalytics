using System.Text;
using Microsoft.Extensions.Options;
using SocAlytics.Platform.Infrastructure.Messaging.Nats;
using Testcontainers.Nats;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.Infrastructure;

/// <summary>A NATS server in operator mode with throwaway trust, a full resolver, the preloaded stamp account, and JetStream.</summary>
public sealed class NatsJwtContainerFixture : IAsyncLifetime
{
    private NatsContainer? _container;

    public string ConnectionString => $"nats://{_container!.Hostname}:{_container.GetMappedPublicPort(4222)}";

    public string OperatorSeed { get; private set; } = string.Empty;

    public string AccountSeed { get; private set; } = string.Empty;

    public string SystemAccountSeed { get; private set; } = string.Empty;

    internal NatsTrust Trust { get; private set; } = null!;

    public IReadOnlyDictionary<string, string?> AuthenticationSettings => new Dictionary<string, string?>
    {
        ["Nats:Authentication:Mode"] = "Jwt",
        ["Nats:Authentication:OperatorSeed"] = OperatorSeed,
        ["Nats:Authentication:AccountSeed"] = AccountSeed,
        ["Nats:Authentication:SystemAccountSeed"] = SystemAccountSeed,
    };

    public async ValueTask InitializeAsync()
    {
        (OperatorSeed, AccountSeed, SystemAccountSeed) = NatsTrust.GenerateSeeds();
        Trust = new NatsTrust(Options.Create(new NatsAuthenticationOptions
        {
            Mode = NatsAuthenticationMode.Jwt,
            OperatorSeed = OperatorSeed,
            AccountSeed = AccountSeed,
            SystemAccountSeed = SystemAccountSeed,
        }));

        var config = $$"""
            port: 4222
            server_name: socalytics-test
            operator: {{Trust.BuildOperatorJwt()}}
            system_account: {{Trust.SystemAccountPublicKey}}
            resolver: {
              type: full
              dir: '/tmp/nats-resolver'
              allow_delete: false
              interval: "2m"
            }
            resolver_preload: {
              {{Trust.StampAccountPublicKey}}: {{Trust.BuildStampAccountJwt()}}
              {{Trust.SystemAccountPublicKey}}: {{Trust.BuildSystemAccountJwt()}}
            }
            jetstream: {
              store_dir: '/tmp/nats-js'
            }
            """;

        _container = new NatsBuilder("nats:2.14")
            .WithResourceMapping(Encoding.UTF8.GetBytes(config), "/etc/nats/nats.conf")
            .WithCommand("-c", "/etc/nats/nats.conf")
            .Build();
        await _container.StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }
}
