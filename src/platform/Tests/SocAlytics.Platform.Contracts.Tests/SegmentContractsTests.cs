using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;
using Shouldly;
using Xunit;

namespace SocAlytics.Platform.Contracts.Tests;

public class SegmentContractsTests
{
    private static readonly string ContractsRoot = Path.Combine(AppContext.BaseDirectory, "contracts");
    private static readonly string ManifestDirectory = Path.Combine(ContractsRoot, "segments", "segment-manifest", "v1");
    private static readonly string MediaDirectory = Path.Combine(ContractsRoot, "analysis", "segment-media", "v1");

    private static IEnumerable<string> ExampleFiles(string directory, string kind) =>
        Directory.EnumerateFiles(Path.Combine(directory, "examples", kind), "*.json")
            .Where(f => Path.GetFileName(f) != "expectations.json")
            .Order(StringComparer.Ordinal);

    private static bool IsValid(string directory, string name, string file)
    {
        var id = JsonNode.Parse(File.ReadAllText(Path.Combine(directory, name + ".schema.json")))!["$id"]!.GetValue<string>();
        var schema = (JsonSchema)ContractCatalog.LoadRegistry().Get(new Uri(id))!;
        var instance = JsonDocument.Parse(File.ReadAllText(file)).RootElement;
        return schema.Evaluate(instance, new EvaluationOptions { OutputFormat = OutputFormat.List, RequireFormatValidation = true }).IsValid;
    }

    private static (long Start, long End) Interval(JsonNode node) =>
        (node["startMs"]!.GetValue<long>(), node["endMs"]!.GetValue<long>());

    private static void AssertManifestRules(JsonNode manifest, string name)
    {
        var duration = manifest["segmentationPolicy"]!["durationSeconds"]!.GetValue<long>() * 1000;
        var number = manifest["identity"]!["segmentNumber"]!.GetValue<long>();

        foreach (var key in new[] { "nominalWindow", "requestedSourceInterval", "encodedInterval" })
        {
            var (start, end) = Interval(manifest[key]!);
            end.ShouldBeGreaterThan(start, $"{name}: {key} endMs > startMs");
        }

        var requested = Interval(manifest["requestedSourceInterval"]!);
        var encoded = Interval(manifest["encodedInterval"]!);
        encoded.Start.ShouldBeLessThanOrEqualTo(requested.Start, $"{name}: encoded interval covers requested start");
        encoded.End.ShouldBeGreaterThanOrEqualTo(requested.End, $"{name}: encoded interval covers requested end");

        var nominal = Interval(manifest["nominalWindow"]!);
        nominal.ShouldBe(((number - 1) * duration, number * duration), $"{name}: nominal window is [(n-1)*D, n*D)");

        var spans = manifest["timelineSpans"]!.AsArray().ToList();
        spans.ShouldNotBeEmpty(name);
        var previousEnd = nominal.Start;
        foreach (var span in spans)
        {
            var mediaStart = span!["mediaStartMs"]!.GetValue<long>();
            var mediaEnd = span["mediaEndMs"]!.GetValue<long>();
            var matchStart = span["matchStartMs"]!.GetValue<long>();
            mediaEnd.ShouldBeGreaterThan(mediaStart, $"{name}: span mediaEndMs > mediaStartMs");
            var matchEnd = matchStart + (mediaEnd - mediaStart);
            matchStart.ShouldBeGreaterThanOrEqualTo(previousEnd, $"{name}: spans ordered and non-overlapping inside the nominal window");
            matchEnd.ShouldBeLessThanOrEqualTo(nominal.End, $"{name}: span inside the nominal window");
            previousEnd = matchEnd;
        }
    }

    [Fact]
    public void Every_valid_example_validates_and_satisfies_the_rules_the_schema_cannot_express()
    {
        var manifests = ExampleFiles(ManifestDirectory, "valid").ToList();
        manifests.Count.ShouldBeGreaterThanOrEqualTo(2);
        foreach (var file in manifests)
        {
            IsValid(ManifestDirectory, "segment-manifest", file).ShouldBeTrue(Path.GetFileName(file));
            AssertManifestRules(JsonNode.Parse(File.ReadAllText(file))!, Path.GetFileName(file));
        }

        var medias = ExampleFiles(MediaDirectory, "valid").ToList();
        medias.ShouldNotBeEmpty();
        foreach (var file in medias)
        {
            IsValid(MediaDirectory, "segment-media", file).ShouldBeTrue(Path.GetFileName(file));
            var media = JsonNode.Parse(File.ReadAllText(file))!;
            AssertManifestRules(media["manifest"]!, Path.GetFileName(file));
            media["grants"]!.AsArray().Select(g => g!["name"]!.GetValue<string>()).Order()
                .ShouldBe(["segment-manifest.json", "segment.mp4"]);
        }
    }

    [Fact]
    public void Every_invalid_example_fails_validation()
    {
        foreach (var file in ExampleFiles(ManifestDirectory, "invalid"))
        {
            IsValid(ManifestDirectory, "segment-manifest", file).ShouldBeFalse(Path.GetFileName(file));
        }

        foreach (var file in ExampleFiles(MediaDirectory, "invalid"))
        {
            IsValid(MediaDirectory, "segment-media", file).ShouldBeFalse(Path.GetFileName(file));
        }
    }

    [Fact]
    public void Segment_media_release_baseline_equals_the_current_schema()
    {
        File.ReadAllText(Path.Combine(MediaDirectory, "releases", "1.0.0.schema.json"))
            .ShouldBe(File.ReadAllText(Path.Combine(MediaDirectory, "segment-media.schema.json")));
    }

    [Fact]
    public void Rule_checks_reject_manifests_that_break_a_rule()
    {
        var file = Path.Combine(ManifestDirectory, "examples", "valid", "clipped-segment-manifest.json");
        string[] mutations =
        [
            "/nominalWindow/startMs=9000",
            "/encodedInterval/startMs=3600500",
            "/encodedInterval/endMs=3702000",
            "/timelineSpans/0/matchStartMs=17000",
            "/timelineSpans/1/matchStartMs=19000",
            "/requestedSourceInterval/endMs=3600000",
        ];
        foreach (var mutation in mutations)
        {
            var parts = mutation.Split('=');
            var manifest = JsonNode.Parse(File.ReadAllText(file))!;
            JsonNode target = manifest;
            var path = parts[0].Split('/', StringSplitOptions.RemoveEmptyEntries);
            foreach (var segment in path[..^1])
            {
                target = target is JsonArray array ? array[int.Parse(segment)]! : target[segment]!;
            }

            target[path[^1]] = long.Parse(parts[1]);
            Should.Throw<ShouldAssertException>(() => AssertManifestRules(manifest, mutation), mutation);
        }
    }
}
