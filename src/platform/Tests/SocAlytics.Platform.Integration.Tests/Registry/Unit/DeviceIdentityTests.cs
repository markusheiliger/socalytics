using System.Text.Json;
using Shouldly;
using SocAlytics.Platform.Domain.Registry;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.Registry.Unit;

public sealed class DeviceIdentityTests
{
	private static string FixturePath(string name) =>
		Path.Combine(AppContext.BaseDirectory, "contracts", "analyst-manager", "registration", "v1", name);

	private static JsonElement Load(string name) => JsonDocument.Parse(File.ReadAllText(FixturePath(name))).RootElement.Clone();

	private static JsonElement PublicKey()
	{
		var key = Load("test-key.jwk.json");
		var json = $"{{\"kty\":\"{key.GetProperty("kty").GetString()}\",\"crv\":\"{key.GetProperty("crv").GetString()}\",\"x\":\"{key.GetProperty("x").GetString()}\",\"y\":\"{key.GetProperty("y").GetString()}\"}}";
		return JsonDocument.Parse(json).RootElement.Clone();
	}

	[Fact]
	public void ThumbprintAndFingerprintMatchFixture()
	{
		var expected = Load("fingerprint.json");
		DeviceKeyThumbprint.TryCreate(PublicKey(), out var thumbprint).ShouldBeTrue();
		thumbprint!.Value.ShouldBe(expected.GetProperty("thumbprint").GetString());
		DeviceFingerprint.From(thumbprint).Value.ShouldBe(expected.GetProperty("deviceFingerprint").GetString());
	}

	[Fact]
	public void PrivateMemberIsRejected()
	{
		DeviceKeyThumbprint.TryCreate(Load("test-key.jwk.json"), out _).ShouldBeFalse();
	}

	[Theory]
	[InlineData("""{"kty":"EC","crv":"P-384","x":"f83OJ3D2xF1Bg8vub9tLe1gHMzV76e8Tus9uPHvRVEU","y":"x_FEzRu9m36HLN_tue659LNpXW6pCyStikYjKIWI5a0"}""")]
	[InlineData("""{"kty":"RSA","crv":"P-256","x":"f83OJ3D2xF1Bg8vub9tLe1gHMzV76e8Tus9uPHvRVEU","y":"x_FEzRu9m36HLN_tue659LNpXW6pCyStikYjKIWI5a0"}""")]
	[InlineData("""{"kty":"EC","crv":"P-256","x":"short","y":"x_FEzRu9m36HLN_tue659LNpXW6pCyStikYjKIWI5a0"}""")]
	[InlineData("""{"kty":"EC","crv":"P-256","x":"f83OJ3D2xF1Bg8vub9tLe1gHMzV76e8Tus9uPHvRVEU"}""")]
	[InlineData("""{"kty":"EC","crv":"P-256","x":"f83OJ3D2xF1Bg8vub9tLe1gHMzV76e8Tus9uPHvRVEU","y":"x_FEzRu9m36HLN_tue659LNpXW6pCyStikYjKIWI5a0","kid":"a"}""")]
	public void WrongCurveOrShapeIsRejected(string json)
	{
		DeviceKeyThumbprint.TryCreate(JsonDocument.Parse(json).RootElement, out _).ShouldBeFalse();
	}

	[Fact]
	public void FingerprintComparisonIgnoresCaseDashesAndSpaces()
	{
		DeviceKeyThumbprint.TryCreate(PublicKey(), out var thumbprint).ShouldBeTrue();
		var fingerprint = DeviceFingerprint.From(thumbprint!);
		fingerprint.Matches("thnn pkjr").ShouldBeTrue();
		fingerprint.Matches("THNNPKJR").ShouldBeTrue();
		fingerprint.Matches("THNN-PKJX").ShouldBeFalse();
		fingerprint.Matches(null).ShouldBeFalse();
		fingerprint.Matches("THNN-PKJ").ShouldBeFalse();
	}

	[Fact]
	public void MetadataAcceptsLimitsAndRejectsViolations()
	{
		DeviceMetadata.TryCreate("linux", new string('a', 64), "x64", new string('1', 32), "tpm", new string('h', 64), out var ok, out _)
			.ShouldBeTrue();
		ok!.ClaimedKeyProtection.ShouldBe(ClaimedKeyProtection.Tpm);

		DeviceMetadata.TryCreate("plan9", new string('a', 65), "mips", new string('1', 33), "hsm", new string('h', 65), out var bad, out var violations)
			.ShouldBeFalse();
		bad.ShouldBeNull();
		violations.Select(v => v.Field).ShouldBe(["os", "osVersion", "architecture", "managerVersion", "claimedKeyProtection", "hostLabel"]);

		DeviceMetadata.TryCreate("macos", "15", "arm64", "1.0", "secure-enclave", null, out var noLabel, out _).ShouldBeTrue();
		noLabel!.HostLabel.ShouldBeNull();
	}
}
