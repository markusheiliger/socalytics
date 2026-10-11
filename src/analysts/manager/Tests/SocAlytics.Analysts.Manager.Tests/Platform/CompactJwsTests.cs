using System.Text.Json.Nodes;
using Shouldly;
using Xunit;
using SocAlytics.Analysts.Manager.Platform;
using SocAlytics.Analysts.Manager.Tests.Support;

namespace SocAlytics.Analysts.Manager.Tests.Platform;

public class CompactJwsTests
{
    [Fact]
    public void Signed_jws_verifies_and_round_trips_claims()
    {
        var key = new InMemoryDeviceKeyProvider().ImportFixtureKey();
        var jws = CompactJws.Sign(key, new JsonObject { ["typ"] = "dpop+jwt" }, new JsonObject { ["jti"] = "abc" });

        CompactJws.TryVerify(jws, key.PublicJwk, out var header, out var payload).ShouldBeTrue();
        ((string)header["alg"]!).ShouldBe("ES256");
        ((string)header["typ"]!).ShouldBe("dpop+jwt");
        ((string)payload["jti"]!).ShouldBe("abc");
    }

    [Fact]
    public async Task Wrong_key_or_tampering_fails()
    {
        var provider = new InMemoryDeviceKeyProvider();
        var key = provider.ImportFixtureKey();
        var other = await provider.CreateKeyAsync(true, TestContext.Current.CancellationToken);
        var jws = CompactJws.Sign(key, new JsonObject(), new JsonObject { ["a"] = 1 });
        var parts = jws.Split('.');

        CompactJws.TryVerify(jws, other.PublicJwk, out _, out _).ShouldBeFalse();
        CompactJws.TryVerify($"{parts[0]}.{parts[0]}.{parts[2]}", key.PublicJwk, out _, out _).ShouldBeFalse();
        CompactJws.TryVerify("garbage", key.PublicJwk, out _, out _).ShouldBeFalse();
    }
}
