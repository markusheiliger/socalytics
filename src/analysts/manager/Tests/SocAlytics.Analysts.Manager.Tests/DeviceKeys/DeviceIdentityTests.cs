using System.Text.Json.Nodes;
using Shouldly;
using Xunit;
using SocAlytics.Analysts.Manager.DeviceKeys;
using SocAlytics.Analysts.Manager.Tests.Support;

namespace SocAlytics.Analysts.Manager.Tests.DeviceKeys;

public class DeviceIdentityTests
{
    [Fact]
    public void Thumbprint_and_fingerprint_of_fixture_key_match_the_fixture()
    {
        var expected = JsonNode.Parse(File.ReadAllText(
            Path.Combine(InMemoryDeviceKeyProvider.FixtureDirectory, "fingerprint.json")))!;
        var key = new InMemoryDeviceKeyProvider().ImportFixtureKey();

        key.Thumbprint.ShouldBe((string)expected["thumbprint"]!);
        DeviceFingerprint.Compute(key.PublicJwk).ShouldBe((string)expected["deviceFingerprint"]!);
    }

    [Fact]
    public void Public_jwk_never_contains_private_members()
    {
        var key = new InMemoryDeviceKeyProvider().ImportFixtureKey();

        key.PublicJwk.ToJsonObject().ContainsKey("d").ShouldBeFalse();
    }
}
