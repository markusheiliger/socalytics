using System.Text.Json.Nodes;
using Shouldly;
using Xunit;

namespace SocAlytics.Platform.Contracts.Tests;

public class ResultContractsTests
{
    private static readonly string AnalysisRoot = Path.Combine(AppContext.BaseDirectory, "contracts", "analysis");

    private static string ManifestSchemaFile => Path.Combine(AnalysisRoot, "result-manifest", "v1", "result-manifest.schema.json");

    [Fact]
    public void Result_manifest_closes_every_object_and_has_a_released_copy()
    {
        var schema = JsonNode.Parse(File.ReadAllText(ManifestSchemaFile))!;
        schema["x-socalytics-version"]!.GetValue<string>().ShouldBe("1.0.0");
        schema["additionalProperties"]!.GetValue<bool>().ShouldBeFalse();
        foreach (var path in new[] { "scope", "provenance", "attempt", "validityWindow" })
        {
            schema["properties"]![path]!["additionalProperties"]!.GetValue<bool>().ShouldBeFalse(path);
        }

        schema["properties"]!["artifacts"]!["items"]!["additionalProperties"]!.GetValue<bool>().ShouldBeFalse();
        schema["properties"]!["upstreamLineage"]!["items"]!["additionalProperties"]!.GetValue<bool>().ShouldBeFalse();

        File.ReadAllBytes(Path.Combine(AnalysisRoot, "result-manifest", "v1", "releases", "1.0.0.schema.json"))
            .ShouldBe(File.ReadAllBytes(ManifestSchemaFile));
    }

    [Fact]
    public void Result_manifest_corpus_covers_both_scopes_and_every_required_refusal()
    {
        var root = Path.Combine(AnalysisRoot, "result-manifest", "v1", "examples");
        var valid = Directory.EnumerateFiles(Path.Combine(root, "valid"), "*.json").Select(f => JsonNode.Parse(File.ReadAllText(f))!).ToList();
        valid.Any(m => m["scope"]!["segment"] != null).ShouldBeTrue("a segment-scope example");
        valid.Any(m => m["scope"]!["match"] != null && m["upstreamLineage"]!.AsArray().Count > 0).ShouldBeTrue("a match-scope example with lineage");

        var expectations = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "invalid", "expectations.json")))!.AsObject();
        foreach (var name in new[]
                 {
                     "missing-scope", "unknown-property", "presigned-url-in-artifact-name", "carries-credential-field",
                     "carries-inline-media", "negative-fact-count", "confidence-above-one", "missing-contract-version",
                 })
        {
            expectations.ContainsKey(name + ".json").ShouldBeTrue(name);
        }
    }

    [Fact]
    public void Result_grants_corpus_covers_every_required_refusal()
    {
        var root = Path.Combine(AnalysisRoot, "result-grants", "v1", "examples");
        File.Exists(Path.Combine(root, "valid", "request.json")).ShouldBeTrue();
        File.Exists(Path.Combine(root, "valid", "response.json")).ShouldBeTrue();

        var expectations = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "invalid", "expectations.json")))!.AsObject();
        foreach (var name in new[] { "duplicate-names", "invalid-name", "invalid-digest", "size-zero" })
        {
            expectations.ContainsKey("request." + name + ".json").ShouldBeTrue(name);
        }
    }
}
