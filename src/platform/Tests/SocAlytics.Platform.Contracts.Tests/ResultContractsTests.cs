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
    public void Upstream_results_closes_every_object_and_has_a_released_copy()
    {
        var dir = Path.Combine(AnalysisRoot, "upstream-results", "v1");
        var file = Path.Combine(dir, "upstream-results.schema.json");
        var schema = JsonNode.Parse(File.ReadAllText(file))!;
        schema["x-socalytics-version"]!.GetValue<string>().ShouldBe("1.1.0");
        schema["additionalProperties"]!.GetValue<bool>().ShouldBeFalse();
        schema["properties"]!["upstream"]!["additionalProperties"]!.GetValue<bool>().ShouldBeFalse();
        schema["properties"]!["upstream"]!["properties"]!["coverage"]!["additionalProperties"]!.GetValue<bool>().ShouldBeFalse();
        var defs = schema["$defs"]!;
        defs["result"]!["additionalProperties"]!.GetValue<bool>().ShouldBeFalse();
        defs["result"]!["properties"]!["scope"]!["additionalProperties"]!.GetValue<bool>().ShouldBeFalse();
        defs["result"]!["properties"]!["grants"]!["items"]!["additionalProperties"]!.GetValue<bool>().ShouldBeFalse();
        defs["coverageEntry"]!["additionalProperties"]!.GetValue<bool>().ShouldBeFalse();
        defs["result"]!["properties"]!["manifest"]!["$ref"]!.GetValue<string>().ShouldEndWith("result-manifest/v1/result-manifest.schema.json");
        defs["coverageEntry"]!["properties"]!["state"]!["enum"]!.AsArray().Select(n => n!.GetValue<string>())
            .ShouldBe(["accepted", "missing", "failed", "blocked", "skipped"]);

        File.ReadAllBytes(Path.Combine(dir, "releases", "1.1.0.schema.json")).ShouldBe(File.ReadAllBytes(file));
        File.Exists(Path.Combine(dir, "releases", "1.0.0.schema.json")).ShouldBeTrue();
    }

    [Fact]
    public void Upstream_results_corpus_covers_partial_coverage_empty_page_and_every_required_refusal()
    {
        var root = Path.Combine(AnalysisRoot, "upstream-results", "v1", "examples");
        var valid = Directory.EnumerateFiles(Path.Combine(root, "valid"), "*.json").Select(f => JsonNode.Parse(File.ReadAllText(f))!).ToList();
        valid.Any(p => p["results"]!.AsArray().Count > 0 && p["coverage"]!.AsArray().Any(c => c!["state"]!.GetValue<string>() != "accepted"))
            .ShouldBeTrue("a page with partial coverage");
        valid.Any(p => p["results"]!.AsArray().Count == 0).ShouldBeTrue("an empty page");

        var expectations = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "invalid", "expectations.json")))!.AsObject();
        foreach (var name in new[] { "unknown-coverage-state", "carries-credential-field", "extra-property", "missing-manifest-checksum" })
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
