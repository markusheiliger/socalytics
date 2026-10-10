using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;
using Shouldly;
using Xunit;

namespace SocAlytics.Platform.Contracts.Tests;

public class AnalystManagerRegistrationFixtureTests
{
    private const string SchemaPath = "contracts/analyst-manager/registration/v1/registration.schema.json";
    private const string SchemaName = "registration.schema.json";
    private const string Alphabet = "BCDFGHJKLMNPQRSTVWXZ";

    private static readonly string Folder =
        Path.Combine(AppContext.BaseDirectory, "contracts", "analyst-manager", "registration", "v1");

    private static JsonNode Read(string name) => JsonNode.Parse(File.ReadAllText(Path.Combine(Folder, name)))!;

    private static IEnumerable<string> ManifestFiles() =>
        Read("fixtures.json")["vectors"]!.AsArray().Select(v => v!["file"]!.GetValue<string>());

    [Fact]
    public void Manifest_validates_against_the_registration_schema()
    {
        var registry = ContractCatalog.LoadRegistry();
        var schema = (JsonSchema)registry.Get(new Uri("https://socalytics.invalid/" + SchemaPath))!;

        var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(Folder, "fixtures.json"))).RootElement;
        var result = schema.Evaluate(manifest, new EvaluationOptions { OutputFormat = OutputFormat.List });

        result.IsValid.ShouldBeTrue();
    }

    [Fact]
    public void Every_manifest_file_exists_and_parses()
    {
        foreach (var file in ManifestFiles())
        {
            File.Exists(Path.Combine(Folder, file)).ShouldBeTrue(file);
            Should.NotThrow(() => Read(file), file);
        }
    }

    [Fact]
    public void Every_folder_file_except_the_schema_is_in_the_manifest()
    {
        var listed = ManifestFiles().ToHashSet(StringComparer.Ordinal);
        var actual = Directory.EnumerateFiles(Folder, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(Folder, f).Replace('\\', '/'))
            .Where(f => f != SchemaName);

        foreach (var file in actual.Where(f => f != "fixtures.json"))
        {
            listed.ShouldContain(file);
        }
    }

    [Fact]
    public void Fingerprint_matches_recomputation_from_the_test_key()
    {
        var key = Read("test-key.jwk.json");
        var canonical = $"{{\"crv\":\"{key["crv"]}\",\"kty\":\"{key["kty"]}\",\"x\":\"{key["x"]}\",\"y\":\"{key["y"]}\"}}";
        var thumbprint = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));

        var n = System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(thumbprint) % 25_600_000_000UL;
        var digits = new char[8];
        for (var i = 7; i >= 0; i--)
        {
            digits[i] = Alphabet[(int)(n % 20)];
            n /= 20;
        }

        var fingerprint = Read("fingerprint.json");
        fingerprint["thumbprint"]!.GetValue<string>()
            .ShouldBe(Convert.ToBase64String(thumbprint).TrimEnd('=').Replace('+', '-').Replace('/', '_'));
        fingerprint["deviceFingerprint"]!.GetValue<string>()
            .ShouldBe(new string(digits, 0, 4) + "-" + new string(digits, 4, 4));
    }

    [Fact]
    public void Only_the_test_key_contains_a_private_member()
    {
        foreach (var file in Directory.EnumerateFiles(Folder, "*.json", SearchOption.AllDirectories))
        {
            var name = Path.GetRelativePath(Folder, file).Replace('\\', '/');
            var hasD = HasPrivateMember(JsonNode.Parse(File.ReadAllText(file)));
            hasD.ShouldBe(name == "test-key.jwk.json", name);
        }
    }

    [Fact]
    public void Every_jws_except_wrong_key_verifies_with_the_test_key()
    {
        var key = Read("test-key.jwk.json");
        using var ecdsa = ECDsa.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = Decode(key["x"]!.GetValue<string>()), Y = Decode(key["y"]!.GetValue<string>()) },
        });
        var manifest = Read("fixtures.json");
        var count = 0;
        foreach (var file in ManifestFiles().Where(f => f.StartsWith("activation.request") || f.Contains("token.request")
                     || f.StartsWith("self-registration.request") || f.StartsWith("negative/")))
        {
            foreach (var jws in FindJws(Read(file)))
            {
                var parts = jws.Split('.');
                var ok = ecdsa.VerifyData(Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]), Decode(parts[2]),
                    HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
                if (file == "negative/wrong-key.json" && Header(jws)["typ"]!.GetValue<string>() == "dpop+jwt")
                {
                    ok.ShouldBeFalse(file);
                }
                else
                {
                    ok.ShouldBeTrue(file);
                }
                count++;
            }
        }
        count.ShouldBeGreaterThan(8);
        manifest["clock"]!.GetValue<string>().ShouldBe("2026-01-01T00:00:00Z");
    }

    [Fact]
    public void Jws_headers_and_claims_match_the_profiles()
    {
        var m = Read("fixtures.json");
        var baseUri = m["baseUri"]!.GetValue<string>();
        var iat = DateTimeOffset.Parse(m["clock"]!.GetValue<string>()).ToUnixTimeSeconds();
        var managerId = m["managerId"]!.GetValue<string>();
        var token = baseUri + "/api/v1/analyst-managers/token";

        var act = Claims(Read("activation.request.json")["proof"]!.GetValue<string>(), out var ah);
        ah["typ"]!.GetValue<string>().ShouldBe("analyst-manager-activation+jwt");
        ah["alg"]!.GetValue<string>().ShouldBe("ES256");
        ah.ContainsKey("jwk").ShouldBeFalse();
        act["aud"]!.GetValue<string>().ShouldBe(baseUri + "/api/v1/analyst-managers/pairings/activation");
        act["challenge"]!.GetValue<string>().ShouldBe(m["challenge"]!.GetValue<string>());
        act["iat"]!.GetValue<long>().ShouldBe(iat);

        var tr = Read("token.request.json");
        var form = tr["form"]!;
        form["grant_type"]!.GetValue<string>().ShouldBe("client_credentials");
        form["client_id"]!.GetValue<string>().ShouldBe(managerId);
        form["client_assertion_type"]!.GetValue<string>().ShouldBe("urn:ietf:params:oauth:client-assertion-type:jwt-bearer");
        var ca = Claims(form["client_assertion"]!.GetValue<string>(), out var ch);
        ch["alg"]!.GetValue<string>().ShouldBe("ES256");
        ca["iss"]!.GetValue<string>().ShouldBe(managerId);
        ca["sub"]!.GetValue<string>().ShouldBe(managerId);
        ca["aud"]!.GetValue<string>().ShouldBe(token);
        (ca["exp"]!.GetValue<long>() - ca["iat"]!.GetValue<long>()).ShouldBe(60);
        var dp = Claims(tr["headers"]!["DPoP"]!.GetValue<string>(), out var dh);
        dh["typ"]!.GetValue<string>().ShouldBe("dpop+jwt");
        dh["jwk"].ShouldNotBeNull();
        dp["htm"]!.GetValue<string>().ShouldBe("POST");
        dp["htu"]!.GetValue<string>().ShouldBe(token);

        var sr = Read("self-registration.request.json")["headers"]!;
        var sc = Claims(sr["DPoP"]!.GetValue<string>(), out _);
        sc["htm"]!.GetValue<string>().ShouldBe("GET");
        sc["htu"]!.GetValue<string>().ShouldBe(baseUri + "/api/v1/analyst-managers/self/registration");
        var access = m["accessToken"]!.GetValue<string>();
        sr["Authorization"]!.GetValue<string>().ShouldBe("DPoP " + access);
        sc["ath"]!.GetValue<string>().ShouldBe(Encode(SHA256.HashData(Encoding.ASCII.GetBytes(access))));

        Claims(Read("negative/stale-iat.json")["headers"]!["DPoP"]!.GetValue<string>(), out _)["iat"]!.GetValue<long>().ShouldBe(iat - 600);
        Claims(Read("negative/missing-ath.json")["headers"]!["DPoP"]!.GetValue<string>(), out _).ContainsKey("ath").ShouldBeFalse();
        Claims(Read("negative/wrong-htu.json")["headers"]!["DPoP"]!.GetValue<string>(), out _)["htu"]!.GetValue<string>().ShouldNotBe(token);
        Claims(Read("negative/wrong-aud.json")["form"]!["client_assertion"]!.GetValue<string>(), out _)["aud"]!.GetValue<string>().ShouldNotBe(token);
        Read("negative/replayed-jti.json")["repeat"]!.GetValue<int>().ShouldBe(2);
    }

    private static byte[] Decode(string s) =>
        Convert.FromBase64String(s.Replace('-', '+').Replace('_', '/').PadRight((s.Length + 3) / 4 * 4, '='));

    private static string Encode(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static JsonObject Header(string jws) => JsonNode.Parse(Decode(jws.Split('.')[0]))!.AsObject();

    private static JsonObject Claims(string jws, out JsonObject header)
    {
        header = Header(jws);
        return JsonNode.Parse(Decode(jws.Split('.')[1]))!.AsObject();
    }

    private static IEnumerable<string> FindJws(JsonNode? node) => node switch
    {
        JsonObject o => o.SelectMany(p => FindJws(p.Value)),
        JsonArray a => a.SelectMany(FindJws),
        JsonValue v when v.TryGetValue<string>(out var s) && s.Split('.').Length == 3 && s.StartsWith("eyJ") => [s],
        _ => [],
    };

    private static bool HasPrivateMember(JsonNode? node) => node switch
    {
        JsonObject o => o.ContainsKey("d") || o.Any(p => HasPrivateMember(p.Value)),
        JsonArray a => a.Any(HasPrivateMember),
        _ => false,
    };
}
