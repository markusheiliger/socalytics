using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using SocAlytics.Platform.Application.Registry;
using SocAlytics.Platform.Integration.Tests.IdentityAccess.Support;
using SocAlytics.Platform.Integration.Tests.Infrastructure;
using SocAlytics.Platform.Integration.Tests.Registry.Support;
using SocAlytics.Platform.Migrator;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.Registry;

// Runs every vector of the shared golden fixtures (contracts/analyst-manager/registration/v1) against the API.
public sealed partial class GoldenFixtureTests(PostgresContainerFixture postgres)
{
    private static readonly string Root = Path.Combine(AppContext.BaseDirectory, "contracts", "analyst-manager", "registration", "v1");

    // Values that are deterministic given the seed; everything else is compared by shape only.
    private static readonly HashSet<string> DeterministicKeys =
    [
        "stampId", "state", "keyThumbprint", "status", 
        "tokenEndpoint", "error", "registration_state", "code", "registrationState", "token_type",
        "expires_in", "scope", "softwareKeysAllowed", "pairingEndpoint", "activationEndpoint", "thumbprint", "deviceFingerprint",
    ];

    // Optional response members: the platform may add or omit them per state.
    private static readonly HashSet<string> OptionalKeys = ["statusCheckIntervalSeconds", "expiresAt"];

    public static TheoryData<string> VectorIds()
    {
        var data = new TheoryData<string>();
        foreach (var v in Manifest().RootElement.GetProperty("vectors").EnumerateArray())
        {
            data.Add(v.GetProperty("id").GetString()!);
        }

        return data;
    }

    private static JsonDocument Manifest() => JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, "fixtures.json")));

    private static JsonElement Load(string file) => JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, file))).RootElement;

    private static byte[] Hash(string value) => SHA256.HashData(Encoding.UTF8.GetBytes(value));

    private static string Snake(string pascal) => PascalBoundary().Replace(pascal, "_$1").ToLowerInvariant();

    [GeneratedRegex("(?<!^)([A-Z])")]
    private static partial Regex PascalBoundary();

    [Theory]
    [MemberData(nameof(VectorIds))]
    public async Task VectorHolds(string id)
    {
        var ct = TestContext.Current.CancellationToken;
        var manifest = Manifest().RootElement;
        var vector = manifest.GetProperty("vectors").EnumerateArray().Single(v => v.GetProperty("id").GetString() == id);
        var exchange = vector.GetProperty("exchange").GetString()!;
        var expected = vector.GetProperty("expected");
        var status = expected.GetProperty("status").GetInt32();
        var clock = DateTimeOffset.Parse(manifest.GetProperty("clock").GetString()!);
        var stampId = manifest.GetProperty("stampId").GetString()!;

        if (exchange is "fingerprint" or "key")
        {
            // Pure data vectors: the fingerprint and thumbprint of the fixture key are verified in Registry/Unit/DeviceIdentityTests.
            Load(vector.GetProperty("file").GetString()!).ValueKind.ShouldBe(JsonValueKind.Object);
            return;
        }

        var db = await postgres.CreateDatabaseAsync(ct);
        await using var dbScope = db;
        (await MigratorHarness.RunAsync(db, TestMigrationCatalogs.Platform(), ct)).ExitCode.ShouldBe(MigratorExitCode.Success);
        await using var factory = new PlatformApiFactory(db, new Dictionary<string, string?>
        {
            ["ClubDisplayName"] = "Fixture Club",
            ["FirstClubAdmin:AccountName"] = "registry-admin",
            ["FirstClubAdmin:InitialPassword"] = "Initial-Admin-Pass-1234",
        }, configureServices: services =>
        {
            services.Configure<RegistryOptions>(o =>
            {
                o.AllowSoftwareKeys = true;
                o.RateLimits.PairingCreationPerMinute = 10_000;
                o.RateLimits.StatusPerMinute = 10_000;
                o.RateLimits.ActivationPerMinute = 10_000;
                o.RateLimits.CodeSubmissionPerMinute = 10_000;
            });
            services.PostConfigure<StampOptions>(o =>
            {
                o.Id = "fixture-stamp";
                o.PublicBaseUri = "https://stamp.example.test";
            });
        });
        factory.Time.Set(clock);
        await factory.WaitUntilHealthyAsync(ct);
        factory.Time.Set(clock);

        if (vector.TryGetProperty("seedState", out var seed))
        {
            await SeedAsync(db, manifest, seed, clock, ct);
        }

        if (exchange == "submission")
        {
            await TestMembers.SeedAsync(db, "admin-1", clubRoles: ["club-admin", "registrar"], cancellationToken: ct);
        }

        using var client = factory.CreateApiClient();
        var file = vector.GetProperty("file").GetString()!;
        var body = Load(file);
        var repeat = body.ValueKind == JsonValueKind.Object && body.TryGetProperty("repeat", out var r) ? r.GetInt32() : 1;
        HttpResponseMessage? response = null;
        for (var i = 0; i < repeat; i++)
        {
            response?.Dispose();
            response = exchange switch
            {
                "stamp-discovery" => await client.GetAsync("/api/v1/analyst-managers/stamp", ct),
                "pairing" => await client.PostAsJsonAsync("/api/v1/analyst-managers/pairings", Load("pairing.request.json"), ct),
                "pairing-status" => await client.PostAsJsonAsync("/api/v1/analyst-managers/pairings/status",
                    new { pollingHandle = manifest.GetProperty("pollingHandle").GetString() }, ct),
                "submission" => await SubmitAsync(factory, body, ct),
                "activation" => await client.PostAsJsonAsync("/api/v1/analyst-managers/pairings/activation",
                    Load("activation.request.json"), ct),
                "token" => await SendTokenAsync(client, Load(id.StartsWith("token-") ? "token.request.json" : file), ct),
                "self" => await SendSelfAsync(client, Load("self-registration.request.json"), ct),
                "pairing-negative" => await client.PostAsJsonAsync("/api/v1/analyst-managers/pairings", body, ct),
                "negative" => body.TryGetProperty("form", out _) ? await SendTokenAsync(client, body, ct) : await SendSelfAsync(client, body, ct),
                _ => throw new InvalidOperationException($"Unknown exchange '{exchange}'."),
            };
        }

        using var final = response!;
        var text = await final.Content.ReadAsStringAsync(ct);
        ((int)final.StatusCode).ShouldBe(status, $"{id}: {text}");
        var actual = text.Length > 0 ? JsonDocument.Parse(text).RootElement : default;

        if (expected.TryGetProperty("code", out var code))
        {
            actual.GetProperty("code").GetString().ShouldBe(code.GetString(), id);
        }

        if (expected.TryGetProperty("error", out var error))
        {
            actual.GetProperty("error").GetString().ShouldBe(error.GetString(), id);
        }

        var reference = ReferenceFor(id, file, exchange);
        if (reference is { } shape)
        {
            if (shape.ValueKind == JsonValueKind.Object && shape.TryGetProperty("headers", out var headers) && shape.TryGetProperty("body", out var inner))
            {
                foreach (var h in headers.EnumerateObject())
                {
                    final.Headers.GetValues(h.Name).ShouldContain(h.Value.GetString()!, id);
                }

                shape = inner;
            }

            CompareShape(shape, actual, "$", id, exchange);
        }
    }

    private static JsonElement? ReferenceFor(string id, string file, string exchange) => id switch
    {
        "pairing-request" => Load("pairing.response.json"),
        "activation-request" => Load("activation.response.json"),
        "token-request" => Load("token.response.json"),
        "self-registration-request" => Load("self-registration.response.json"),
        "submission-valid" => null,
        "submission-wrong-fingerprint" => null,
        _ when exchange is "negative" or "pairing-negative" => null,
        _ => Load(file),
    };

    private static void CompareShape(JsonElement expected, JsonElement actual, string path, string id, string exchange)
    {
        var name = path[(path.LastIndexOf('.') + 1)..];
        actual.ValueKind.ShouldBe(expected.ValueKind, $"{id}: {path}");
        if (name == "managerId")
        {
            Guid.TryParse(actual.GetString(), out _).ShouldBeTrue($"{id}: {path}");
            return;
        }

        if (name == "challengeExpiresAt" || name == "expiresAt" && exchange == "pairing")
        {
            DateTimeOffset.TryParse(actual.GetString(), out _).ShouldBeTrue($"{id}: {path}");
            return;
        }

        if (name.EndsWith("At", StringComparison.Ordinal))
        {
            DateTimeOffset.Parse(actual.GetString()!).ShouldBe(DateTimeOffset.Parse(expected.GetString()!), $"{id}: {path}");
            return;
        }

        switch (expected.ValueKind)
        {
            case JsonValueKind.Object:
                // Null members are absent on the wire contract; statusCheckIntervalSeconds is optional.
                var present = actual.EnumerateObject().Where(p => p.Value.ValueKind != JsonValueKind.Null).Select(p => p.Name).ToHashSet();
                var required = expected.EnumerateObject().Select(p => p.Name).Where(n => !OptionalKeys.Contains(n)).Order();
                present.Where(n => !OptionalKeys.Contains(n)).Order().ShouldBe(required, $"{id}: {path}");
                foreach (var p in expected.EnumerateObject().Where(p => present.Contains(p.Name)))
                {
                    CompareShape(p.Value, actual.GetProperty(p.Name), $"{path}.{p.Name}", id, exchange);
                }

                break;
            case JsonValueKind.Array:
                actual.GetArrayLength().ShouldBe(expected.GetArrayLength(), $"{id}: {path}");
                break;
            default:
                if (DeterministicKeys.Contains(name))
                {
                    actual.GetRawText().ShouldBe(expected.GetRawText(), $"{id}: {path}");
                }

                break;
        }
    }

    private static async Task<HttpResponseMessage> SubmitAsync(PlatformApiFactory factory, JsonElement body, CancellationToken ct)
    {
        using var admin = await ApiSession.SignInAsync(factory, "admin-1", TestMembers.DefaultPassword, ct);
        return await admin.SendAsync(HttpMethod.Post, "/api/v1/analyst-manager-registrations", JsonSerializer.Deserialize<object>(body.GetRawText()), ct);
    }

    private static Task<HttpResponseMessage> SendTokenAsync(HttpClient client, JsonElement body, CancellationToken ct)
    {
        var form = body.GetProperty("form").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!);
        var guid = ManagerGuid(Manifest().RootElement).ToString();
        form["client_assertion"] = ResignAssertion(form["client_assertion"], guid);
        form["client_id"] = guid;
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/analyst-managers/token") { Content = new FormUrlEncodedContent(form) };
        request.Headers.Add("DPoP", body.GetProperty("headers").GetProperty("DPoP").GetString());
        return client.SendAsync(request, ct);
    }

    // The fixture manager id is a readable string while the platform issues uuids: the assertion identity claims are
    // rewritten and re-signed with the public RFC 7515 test key; every other header and claim stays as in the fixture.
    private static string ResignAssertion(string jwt, string managerId)
    {
        var parts = jwt.Split('.');
        var claims = JsonNode.Parse(Microsoft.IdentityModel.Tokens.Base64UrlEncoder.Decode(parts[1]))!.AsObject();
        claims["iss"] = managerId;
        claims["sub"] = managerId;
        var key = Load("test-key.jwk.json");
        using var ecdsa = ECDsa.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            D = Microsoft.IdentityModel.Tokens.Base64UrlEncoder.DecodeBytes(key.GetProperty("d").GetString()),
            Q = new ECPoint
            {
                X = Microsoft.IdentityModel.Tokens.Base64UrlEncoder.DecodeBytes(key.GetProperty("x").GetString()),
                Y = Microsoft.IdentityModel.Tokens.Base64UrlEncoder.DecodeBytes(key.GetProperty("y").GetString()),
            },
        });
        var input = parts[0] + "." + Microsoft.IdentityModel.Tokens.Base64UrlEncoder.Encode(claims.ToJsonString());
        var signature = ecdsa.SignData(Encoding.ASCII.GetBytes(input), HashAlgorithmName.SHA256);
        return input + "." + Microsoft.IdentityModel.Tokens.Base64UrlEncoder.Encode(signature);
    }

    private static Task<HttpResponseMessage> SendSelfAsync(HttpClient client, JsonElement body, CancellationToken ct)
    {
        var headers = body.GetProperty("headers");
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/analyst-managers/self/registration");
        request.Headers.Authorization = AuthenticationHeaderValue.Parse(headers.GetProperty("Authorization").GetString()!);
        request.Headers.Add("DPoP", headers.GetProperty("DPoP").GetString());
        return client.SendAsync(request, ct);
    }

    private static async Task SeedAsync(IsolatedDatabase db, JsonElement manifest, JsonElement seed, DateTimeOffset clock, CancellationToken ct)
    {
        var state = Snake(seed.GetProperty("registration").GetString()!);
        var registrationId = Guid.Parse(manifest.GetProperty("registrationId").GetString()!);
        var pairing = Load("pairing.request.json");
        var key = Load("test-key.jwk.json");
        var publicJwk = JsonSerializer.Serialize(new { kty = "EC", crv = "P-256", x = key.GetProperty("x").GetString(), y = key.GetProperty("y").GetString() });
        var fingerprint = Load("fingerprint.json");

        var hasManager = state is "active" or "unregistering" or "unregistered" or "revoked";
        Guid? managerId = hasManager || seed.TryGetProperty("managerId", out _) ? ManagerGuid(manifest) : null;
        var ended = state is "revoked" or "unregistered" or "rejected";
        var decided = state is "approved" or "rejected" or "active" or "revoked";

        await using var c = new NpgsqlConnection(db.AppConnectionString);
        await c.OpenAsync(ct);
        await using (var cmd = new NpgsqlCommand(
            "INSERT INTO socalytics.analyst_manager_registration (id, manager_id, stamp_id, state, public_jwk, key_thumbprint, " +
            "device_fingerprint, pairing_nonce, device_metadata, pairing_code_hash, polling_handle_hash, challenge_hash, " +
            "challenge_expires_at, pairing_expires_at, approval_expires_at, activation_expires_at, submitted_at, decided_at, " +
            "decision_reason, activated_at, ended_at, end_reason, correlation_id, created_at) VALUES " +
            "(@id, @mid, @stamp, @state, @jwk::jsonb, @thumb, @fp, @nonce, @meta::jsonb, @code, @handle, @chash, @cexp, @pexp, " +
            "@aexp, @actexp, @sub, @dec, @reason, @act, @end, @endr, 'fixture-correlation', @created)", c))
        {
            cmd.Parameters.AddWithValue("id", registrationId);
            cmd.Parameters.AddWithValue("mid", (object?)managerId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("stamp", manifest.GetProperty("stampId").GetString()!);
            cmd.Parameters.AddWithValue("state", state);
            cmd.Parameters.AddWithValue("jwk", publicJwk);
            cmd.Parameters.AddWithValue("thumb", fingerprint.GetProperty("thumbprint").GetString()!);
            cmd.Parameters.AddWithValue("fp", fingerprint.GetProperty("deviceFingerprint").GetString()!);
            cmd.Parameters.AddWithValue("nonce", pairing.GetProperty("nonce").GetString()!.PadRight(16, '0'));
            cmd.Parameters.AddWithValue("meta", pairing.GetProperty("deviceMetadata").GetRawText());
            cmd.Parameters.AddWithValue("code", state == "pairing" ? Hash("BCDFGHJKLMNP") : DBNull.Value);
            cmd.Parameters.AddWithValue("handle", Hash(manifest.GetProperty("pollingHandle").GetString()!));
            var withChallenge = seed.TryGetProperty("challenge", out _);
            cmd.Parameters.AddWithValue("chash", withChallenge ? Hash(manifest.GetProperty("challenge").GetString()!) : DBNull.Value);
            cmd.Parameters.AddWithValue("cexp", withChallenge ? clock.AddMinutes(2) : DBNull.Value);
            cmd.Parameters.AddWithValue("pexp", clock.AddMinutes(15));
            cmd.Parameters.AddWithValue("aexp", state == "pairing" ? DBNull.Value : clock.AddDays(1));
            cmd.Parameters.AddWithValue("actexp", state == "approved" ? clock.AddDays(1) : DBNull.Value);
            cmd.Parameters.AddWithValue("sub", state == "pairing" ? DBNull.Value : clock);
            cmd.Parameters.AddWithValue("dec", decided ? clock : DBNull.Value);
            cmd.Parameters.AddWithValue("reason", state == "rejected" ? "fixture rejection" : DBNull.Value);
            cmd.Parameters.AddWithValue("act", state is "active" or "revoked" ? clock : DBNull.Value);
            cmd.Parameters.AddWithValue("end", ended ? clock : DBNull.Value);
            cmd.Parameters.AddWithValue("endr", state == "revoked" ? "fixture revocation" : DBNull.Value);
            cmd.Parameters.AddWithValue("created", clock);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        // Revocation deletes every token, so an inactive registration keeps none.
        if (seed.TryGetProperty("accessToken", out _) && !ended)
        {
            await using var cmd = new NpgsqlCommand(
                "INSERT INTO socalytics.analyst_manager_access_token (token_hash, registration_id, jkt, scope, issued_at, expires_at) " +
                "VALUES (@h, @r, @j, 'analyst-manager', @i, @e)", c);
            cmd.Parameters.AddWithValue("h", Hash(manifest.GetProperty("accessToken").GetString()!));
            cmd.Parameters.AddWithValue("r", registrationId);
            cmd.Parameters.AddWithValue("j", fingerprint.GetProperty("thumbprint").GetString()!);
            cmd.Parameters.AddWithValue("i", clock);
            cmd.Parameters.AddWithValue("e", clock.AddMinutes(5));
            await cmd.ExecuteNonQueryAsync(ct);
        }

    }

    // The manifest managerId is a readable string; the platform stores a uuid, so the fixture id maps to a fixed uuid.
    private static Guid ManagerGuid(JsonElement manifest) => new(MD5.HashData(Encoding.UTF8.GetBytes(manifest.GetProperty("managerId").GetString()!)));
}
