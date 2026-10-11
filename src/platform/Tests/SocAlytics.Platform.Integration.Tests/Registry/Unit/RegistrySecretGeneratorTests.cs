using Microsoft.Extensions.Configuration;
using Shouldly;
using SocAlytics.Platform.Application.Registry;
using SocAlytics.Platform.Infrastructure.Registry;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.Registry.Unit;

public sealed class RegistrySecretGeneratorTests
{
    private readonly RegistrySecretGenerator _generator = new();

    [Fact]
    public void PairingCodeUsesAlphabetAndLength()
    {
        for (var i = 0; i < 200; i++)
        {
            var code = _generator.NewPairingCode();
            code.Length.ShouldBe(12);
            code.ShouldAllBe(c => "BCDFGHJKLMNPQRSTVWXZ".Contains(c));
        }
    }

    [Fact]
    public void FormatAndNormalizeRoundTrip()
    {
        _generator.FormatPairingCode("bcdfghjklmnp").ShouldBe("BCDF-GHJK-LMNP");
        _generator.NormalizePairingCode(" bcdf-ghjk lmnp ").ShouldBe("BCDFGHJKLMNP");
        _generator.HashPairingCode("bcdf-ghjk-lmnp").ShouldBe(_generator.HashPairingCode("BCDFGHJKLMNP"));
    }

    [Fact]
    public void SecretsAreUniqueBase64UrlOf256Bits()
    {
        var values = Enumerable.Range(0, 50).Select(_ => _generator.NewAccessToken()).ToList();
        values.Distinct().Count().ShouldBe(50);
        values.ShouldAllBe(v => v.Length == 43 && !v.Contains('+') && !v.Contains('/') && !v.Contains('='));
        _generator.NewPollingHandle().Length.ShouldBe(43);
        _generator.NewChallenge().Length.ShouldBe(43);
    }

    [Fact]
    public void HashIsStable()
    {
        _generator.Hash("abc").ShouldBe(_generator.Hash("abc"));
        _generator.Hash("abc").Length.ShouldBe(32);
        _generator.Hash("abc").ShouldNotBe(_generator.Hash("abd"));
    }

    [Fact]
    public void OptionDefaultsMatchDataModel()
    {
        var o = new RegistryOptions();
        o.AllowSoftwareKeys.ShouldBeFalse();
        o.Pairing.MaxFingerprintMismatches.ShouldBe(3);
        o.Pairing.Window.ShouldBe(TimeSpan.FromMinutes(10));
        o.Pairing.ApprovalWindow.ShouldBe(TimeSpan.FromHours(24));
        o.Pairing.VerificationPath.ShouldBe("/analyst-managers/pair");
        o.Tokens.Lifetime.ShouldBe(TimeSpan.FromMinutes(5));
        o.RateLimits.PairingCreationPerMinute.ShouldBe(10);
        o.RateLimits.TokenPerMinute.ShouldBe(30);
        o.ExpiryWorker.Interval.ShouldBe(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void OptionsBindAndStampBuildsUris()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Registry:Pairing:Window"] = "00:02:00",
            ["Stamp:Id"] = "s",
            ["Stamp:PublicBaseUri"] = "https://x.example/",
        }).Build();
        var registry = config.GetSection("Registry").Get<RegistryOptions>()!;
        var stamp = config.GetSection("Stamp").Get<StampOptions>()!;
        registry.Pairing.Window.ShouldBe(TimeSpan.FromMinutes(2));
        stamp.TokenUri.ToString().ShouldBe("https://x.example/api/v1/analyst-managers/token");
        stamp.VerificationUri(registry).ToString().ShouldBe("https://x.example/analyst-managers/pair");
    }
}
