using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;
using SocAlytics.Analysts.Manager.DeviceKeys;
using SocAlytics.Analysts.Manager.Platform;
using SocAlytics.Analysts.Manager.Tests.Support;

namespace SocAlytics.Analysts.Manager.Tests.GoldenFixtures;

public class GoldenFixtureTests
{
    private static readonly string Dir = InMemoryDeviceKeyProvider.FixtureDirectory;
    private static readonly JsonObject Manifest = Load("fixtures.json");

    private readonly IDeviceKey _key;
    private readonly PlatformClient _client;
    private readonly Uri _baseUri = new((string)Manifest["baseUri"]!);

    public GoldenFixtureTests()
    {
        _key = new InMemoryDeviceKeyProvider().ImportFixtureKey();
        var clock = new FakeTimeProvider(DateTimeOffset.Parse((string)Manifest["clock"]!));
        _client = new PlatformClient(new HttpClient(), new ProofFactory(clock));
    }

    private static JsonObject Load(string file) => JsonNode.Parse(File.ReadAllText(Path.Combine(Dir, file)))!.AsObject();

    private static T Read<T>(string file) =>
        JsonSerializer.Deserialize<T>(File.ReadAllText(Path.Combine(Dir, file)), PlatformJson.Options)!;

    [Fact]
    public void Response_vectors_deserialize()
    {
        Read<StampDiscovery>("stamp-discovery.response.json").StampId.ShouldBe("fixture-stamp");
        Read<PairingIssued>("pairing.response.json").PollingHandle.ShouldNotBeEmpty();
        foreach (var s in new[] { "awaiting", "pending", "approved", "rejected" })
        {
            Read<PairingStatus>($"pairing-status.{s}.response.json").Status.ShouldNotBeEmpty();
        }

        Read<PairingStatus>("pairing-status.approved.response.json").Challenge.ShouldBe((string)Manifest["challenge"]!);
        Read<ActivationResult>("activation.response.json").ManagerId.ShouldBe((string)Manifest["managerId"]!);
        Read<TokenResponse>("token.response.json").AccessToken.ShouldBe((string)Manifest["accessToken"]!);
        Read<OAuthError>("token.inactive.response.json").RegistrationState.ShouldBe("revoked");
        Read<OwnRegistration>("self-registration.response.json").State.ShouldBe("active");
        var inactive = Load("self-registration.inactive.response.json");
        JsonSerializer.Deserialize<Problem>(inactive["body"]!.ToJsonString(), PlatformJson.Options)!
            .RegistrationState.ShouldBe("revoked");
    }

    [Theory]
    [InlineData("accepted-with-minimum.response.json")]
    [InlineData("accepted-without-minimum.response.json")]
    public void Status_response_examples_deserialize(string file)
    {
        var path = Path.Combine(Dir, "..", "..", "status", "v1", "examples", "valid", file);
        var response = JsonSerializer.Deserialize<OwnRegistration>(File.ReadAllText(path), PlatformJson.Options)!;
        response.State.ShouldBe("active");
        response.ReportAccepted.ShouldBe(true);
    }

    [Fact]
    public async Task Pairing_request_matches()
    {
        var expected = Load("pairing.request.json");
        var m = expected["deviceMetadata"]!.AsObject();
        var metadata = new DeviceMetadata((string)m["os"]!, (string)m["claimedKeyProtection"]!, (string)m["osVersion"]!,
            (string)m["architecture"]!, (string)m["managerVersion"]!, null, (string?)m["claimedApplicationProfile"]);
        using var request = _client.BuildPairing(_baseUri, (string)Manifest["stampId"]!, _key, metadata);

        request.Method.ShouldBe(HttpMethod.Post);
        request.RequestUri!.AbsolutePath.ShouldBe("/api/v1/analyst-managers/pairings");
        var actual = JsonNode.Parse(await request.Content!.ReadAsStringAsync(TestContext.Current.CancellationToken))!.AsObject();
        actual["nonce"]!.GetValue<string>().Length.ShouldBeGreaterThanOrEqualTo(16);
        actual.Remove("nonce");
        expected.Remove("nonce");
        JsonNode.DeepEquals(actual, expected).ShouldBeTrue(actual.ToJsonString());
    }

    [Fact]
    public async Task Activation_request_matches()
    {
        var expected = Load("activation.request.json");
        var vector = Decode((string)expected["proof"]!);
        using var request = _client.BuildActivation(
            _baseUri, _key, (string)Manifest["pollingHandle"]!, (string)Manifest["challenge"]!);

        request.RequestUri!.AbsolutePath.ShouldBe("/api/v1/analyst-managers/pairings/activation");
        var actual = JsonNode.Parse(await request.Content!.ReadAsStringAsync(TestContext.Current.CancellationToken))!.AsObject();
        ((string)actual["pollingHandle"]!).ShouldBe((string)expected["pollingHandle"]!);
        AssertJws((string)actual["proof"]!, vector);
    }

    [Fact]
    public async Task Token_request_matches()
    {
        var expected = Load("token.request.json");
        using var request = _client.BuildToken(_baseUri, _key, (string)Manifest["managerId"]!);

        request.RequestUri!.AbsolutePath.ShouldBe("/api/v1/analyst-managers/token");
        var fields = (await request.Content!.ReadAsStringAsync(TestContext.Current.CancellationToken))
            .Split('&').Select(p => p.Split('=')).ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1].Replace('+', ' ')));
        var form = expected["form"]!.AsObject();
        foreach (var (name, value) in form)
        {
            if (name == "client_assertion")
            {
                AssertJws(fields[name], Decode((string)value!));
            }
            else
            {
                fields[name].ShouldBe((string)value!);
            }
        }

        fields.Count.ShouldBe(form.Count);
        AssertJws(request.Headers.GetValues("DPoP").Single(), Decode((string)expected["headers"]!["DPoP"]!));
    }

    [Fact]
    public void Self_registration_request_matches()
    {
        var expected = Load("self-registration.request.json")["headers"]!.AsObject();
        using var request = _client.BuildSelfRegistration(_baseUri, _key, (string)Manifest["accessToken"]!);

        request.Method.ShouldBe(HttpMethod.Get);
        request.RequestUri!.AbsolutePath.ShouldBe("/api/v1/analyst-managers/self/registration");
        request.Headers.GetValues("Authorization").Single().ShouldBe((string)expected["Authorization"]!);
        AssertJws(request.Headers.GetValues("DPoP").Single(), Decode((string)expected["DPoP"]!));
    }

    private static (JsonObject Header, JsonObject Payload) Decode(string jws)
    {
        var parts = jws.Split('.');
        return (
            JsonNode.Parse(PublicJwk.FromBase64Url(parts[0]))!.AsObject(),
            JsonNode.Parse(PublicJwk.FromBase64Url(parts[1]))!.AsObject());
    }

    private void AssertJws(string actual, (JsonObject Header, JsonObject Payload) vector)
    {
        CompactJws.TryVerify(actual, _key.PublicJwk, out var header, out var payload).ShouldBeTrue();
        payload["jti"]!.GetValue<string>().ShouldNotBeEmpty();
        payload.Remove("jti");
        vector.Payload.Remove("jti");
        JsonNode.DeepEquals(header, vector.Header).ShouldBeTrue(header.ToJsonString());
        JsonNode.DeepEquals(payload, vector.Payload).ShouldBeTrue(payload.ToJsonString());
    }
}
