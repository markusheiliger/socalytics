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

    private static bool HasPrivateMember(JsonNode? node) => node switch
    {
        JsonObject o => o.ContainsKey("d") || o.Any(p => HasPrivateMember(p.Value)),
        JsonArray a => a.Any(HasPrivateMember),
        _ => false,
    };
}
