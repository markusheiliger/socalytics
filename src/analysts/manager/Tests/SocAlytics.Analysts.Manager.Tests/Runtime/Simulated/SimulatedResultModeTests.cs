using System.Text.Json.Nodes;
using Shouldly;
using SocAlytics.Analysts.Manager.Platform;
using SocAlytics.Analysts.Manager.Runtime;
using SocAlytics.Analysts.Manager.Runtime.Simulated;
using SocAlytics.Analysts.Manager.Work;
using Xunit;

namespace SocAlytics.Analysts.Manager.Tests.Runtime.Simulated;

public sealed class SimulatedResultModeTests : IDisposable
{
    private const string Capability = "match-summary";
    private static readonly string UpstreamChecksum = "sha-256:" + new string('1', 64);

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "simulated-modes-test-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, true);
        }
    }

    private static AnalystJobMessage Job() => new(
        Guid.NewGuid().ToString(), Guid.NewGuid().ToString(), "run-1", "node-1", Guid.NewGuid().ToString(), "team-1", "match-1",
        new QueueCapability(Capability, "1.0.0"), "1.0.0", "match",
        [new JobInput([new JobAcceptedResult("res-1", new QueueCapability("player-detection", "1.0.0"), "1.0.0", UpstreamChecksum)])],
        new JobPinned("sha-256:" + new string('3', 64), "sha-256:" + new string('4', 64), null, "profile-1", []),
        new JobResources(500, 1024), new CompletionCorrelation("corr-1"), "rsv-1");

    private JsonNode Write(string? mode, out AnalystJobMessage job, string? inputs = null)
    {
        job = Job();
        var options = new SimulatedAnalystOptions();
        if (mode is not null)
        {
            options.Capabilities[Capability] = new SimulatedCapabilityOptions { ResultMode = mode };
        }

        SimulatedResultWriter.Write(
            new AttemptLaunch(
                Guid.NewGuid().ToString(), job.LogicalJobId, Capability, "d", "match", "p", "i", "m",
                new AttemptResources(1, 1, []), job, 1, _directory, inputs),
            options);
        return JsonNode.Parse(File.ReadAllText(Path.Combine(_directory, "manifest.json")))!;
    }

    private bool IsSchemaValid() => ResultManifestSchema.IsValid(File.ReadAllBytes(Path.Combine(_directory, "manifest.json")));

    [Theory]
    [InlineData(null)]
    [InlineData(SimulatedResultModes.Valid)]
    public void Valid_mode_writes_a_schema_valid_manifest_with_its_artifact_and_job_lineage(string? mode)
    {
        var manifest = Write(mode, out var job);

        IsSchemaValid().ShouldBeTrue();
        File.Exists(Path.Combine(_directory, "facts.json")).ShouldBeTrue();
        ((string)manifest["scope"]!["runId"]!).ShouldBe(job.RunId);
        ((string)manifest["provenance"]!["imageDigest"]!).ShouldBe(job.Pinned.ImageDigest);
        ((string)manifest["upstreamLineage"]![0]!["manifestChecksum"]!).ShouldBe(UpstreamChecksum);
        manifest["artifacts"]!.AsArray().Count.ShouldBe(1);
        ((string)manifest["artifacts"]![0]!["mediaType"]!).ShouldBe("application/vnd.socalytics.facts+json");
        ((int)manifest["factCount"]!).ShouldBe(3);

        var facts = ReadFacts()["facts"]!.AsArray();
        facts.Count.ShouldBe(3);
        var start = (long)manifest["validityWindow"]!["startMs"]!;
        var end = (long)manifest["validityWindow"]!["endMs"]!;
        foreach (var fact in facts)
        {
            ((string)fact!["kind"]!).ShouldBe("simulated-concept");
            ((double)fact["confidence"]!).ShouldBe(0.9);
            ((long)fact["startMs"]!).ShouldBeGreaterThanOrEqualTo(start);
            ((long)fact["endMs"]!).ShouldBeLessThanOrEqualTo(end);
            ((long)fact["endMs"]!).ShouldBeGreaterThan((long)fact["startMs"]!);
        }
    }

    private JsonNode ReadFacts() => JsonNode.Parse(File.ReadAllText(Path.Combine(_directory, "facts.json")))!;

    [Fact]
    public void Invalid_facts_writes_one_fact_outside_the_window()
    {
        var manifest = Write(SimulatedResultModes.InvalidFacts, out _);

        IsSchemaValid().ShouldBeTrue();
        var end = (long)manifest["validityWindow"]!["endMs"]!;
        ReadFacts()["facts"]!.AsArray().Count(f => (long)f!["endMs"]! > end).ShouldBe(1);
    }

    [Fact]
    public void Scope_mismatch_names_another_run()
    {
        var manifest = Write(SimulatedResultModes.ScopeMismatch, out var job);

        IsSchemaValid().ShouldBeTrue();
        ((string)manifest["scope"]!["runId"]!).ShouldNotBe(job.RunId);
    }

    [Fact]
    public void Provenance_mismatch_names_another_image()
    {
        var manifest = Write(SimulatedResultModes.ProvenanceMismatch, out var job);

        IsSchemaValid().ShouldBeTrue();
        ((string)manifest["provenance"]!["imageDigest"]!).ShouldNotBe(job.Pinned.ImageDigest);
    }

    [Fact]
    public void Lineage_mismatch_alters_the_upstream_checksum()
    {
        var manifest = Write(SimulatedResultModes.LineageMismatch, out _);

        IsSchemaValid().ShouldBeTrue();
        ((string)manifest["upstreamLineage"]![0]!["manifestChecksum"]!).ShouldNotBe(UpstreamChecksum);
    }

    [Fact]
    public void Artifact_missing_references_an_artifact_that_was_not_written()
    {
        var manifest = Write(SimulatedResultModes.ArtifactMissing, out _);

        IsSchemaValid().ShouldBeTrue();
        manifest["artifacts"]!.AsArray().Count.ShouldBe(2);
        Directory.EnumerateFiles(_directory).Select(Path.GetFileName).Order().ShouldBe(["facts.json", "manifest.json"]);
    }

    [Fact]
    public void Validity_invalid_starts_after_it_ends()
    {
        var manifest = Write(SimulatedResultModes.ValidityInvalid, out _);

        IsSchemaValid().ShouldBeTrue();
        ((long)manifest["validityWindow"]!["startMs"]!).ShouldBeGreaterThan((long)manifest["validityWindow"]!["endMs"]!);
    }

    [Fact]
    public void Contract_invalid_violates_the_manifest_schema()
    {
        Write(SimulatedResultModes.ContractInvalid, out _);

        IsSchemaValid().ShouldBeFalse();
    }

    [Fact]
    public void Unknown_mode_is_refused()
    {
        Should.Throw<InvalidOperationException>(() => Write("nonsense", out _));
    }

    private string StageUpstream(string resultId, string checksumSeed, string coverageJson)
    {
        var inputs = Path.Combine(_directory + "-inputs");
        var capability = Path.Combine(inputs, "player-detection");
        Directory.CreateDirectory(Path.Combine(capability, resultId));
        var manifest = $$"""{"provenance":{"capability":{"id":"player-detection","version":"1.0.0"},"schemaVersion":"1.0.0"},"seed":"{{checksumSeed}}"}""";
        File.WriteAllText(Path.Combine(capability, resultId, "manifest.json"), manifest);
        File.WriteAllText(Path.Combine(capability, "coverage.json"), coverageJson);
        return inputs;
    }

    [Fact]
    public void Lineage_comes_from_the_staged_manifests_and_the_consumed_coverage_is_recorded()
    {
        var inputs = StageUpstream("staged-res-1", "a", """{"upstream":{},"coverage":[{"segmentId":"s-1","state":"accepted"},{"segmentId":"s-2","state":"missing"}]}""");
        try
        {
            var manifest = Write(null, out _, inputs);

            IsSchemaValid().ShouldBeTrue();
            var lineage = manifest["upstreamLineage"]!.AsArray();
            lineage.Count.ShouldBe(1);
            ((string)lineage[0]!["acceptedResultId"]!).ShouldBe("staged-res-1");
            ((string)lineage[0]!["capability"]!["id"]!).ShouldBe("player-detection");
            ((string)lineage[0]!["schemaVersion"]!).ShouldBe("1.0.0");
            ((string)lineage[0]!["manifestChecksum"]!).ShouldBe(
                ResultPublisher.Digest(File.ReadAllBytes(Path.Combine(inputs, "player-detection", "staged-res-1", "manifest.json"))));

            var artifact = JsonNode.Parse(File.ReadAllText(Path.Combine(_directory, "inputs.json")))!;
            var coverage = artifact["consumedCoverage"]!["player-detection"]!.AsArray();
            coverage.Count.ShouldBe(2);
            ((string)coverage[1]!["state"]!).ShouldBe("missing");
            var artifacts = manifest["artifacts"]!.AsArray();
            artifacts.Select(a => (string)a!["name"]!).ShouldBe(["facts.json", "inputs.json"]);
            ((string)artifacts[1]!["mediaType"]!).ShouldBe("application/json");
        }
        finally
        {
            Directory.Delete(inputs, true);
        }
    }

    private const string PartialCoverage =
        """{"upstream":{"capability":{"id":"player-detection","version":"1.0.0"},"schemaVersion":"1.0.0","edgeKind":"required"},"mode":"partial","coverage":[{"segmentId":"s-1","state":"failed"},{"segmentId":"s-2","state":"accepted"}]}""";

    [Fact]
    public void Valid_mode_writes_a_1_1_0_manifest_with_input_coverage_from_every_staged_coverage_file()
    {
        var inputs = StageUpstream("staged-res-1", "a", PartialCoverage);
        try
        {
            var manifest = Write(null, out _, inputs);

            IsSchemaValid().ShouldBeTrue();
            ((string)manifest["contractVersion"]!).ShouldBe("1.1.0");
            var report = manifest["inputCoverage"]!.AsArray().Single()!;
            ((string)report["capability"]!["id"]!).ShouldBe("player-detection");
            ((string)report["schemaVersion"]!).ShouldBe("1.0.0");
            var units = report["units"]!.AsArray();
            units.Count.ShouldBe(2);
            ((string)units[0]!["segmentId"]!).ShouldBe("s-1");
            ((string)units[0]!["state"]!).ShouldBe("failed");
            ((string)units[1]!["state"]!).ShouldBe("accepted");
        }
        finally
        {
            Directory.Delete(inputs, true);
        }
    }

    [Fact]
    public void Without_staged_inputs_no_input_coverage_is_reported()
    {
        var manifest = Write(null, out _);

        manifest["inputCoverage"].ShouldBeNull();
    }

    [Fact]
    public void Coverage_mismatch_flips_one_unit_state()
    {
        var inputs = StageUpstream("staged-res-1", "a", PartialCoverage);
        try
        {
            var manifest = Write(SimulatedResultModes.CoverageMismatch, out _, inputs);

            IsSchemaValid().ShouldBeTrue();
            var units = manifest["inputCoverage"]![0]!["units"]!.AsArray();
            ((string)units[0]!["state"]!).ShouldBe("accepted");
            ((string)units[1]!["state"]!).ShouldBe("accepted");
        }
        finally
        {
            Directory.Delete(inputs, true);
        }
    }

    [Fact]
    public void Coverage_omitted_drops_the_input_coverage()
    {
        var inputs = StageUpstream("staged-res-1", "a", PartialCoverage);
        try
        {
            var manifest = Write(SimulatedResultModes.CoverageOmitted, out _, inputs);

            IsSchemaValid().ShouldBeTrue();
            manifest["inputCoverage"].ShouldBeNull();
        }
        finally
        {
            Directory.Delete(inputs, true);
        }
    }

    [Fact]
    public void Ignore_inputs_reports_an_empty_lineage_although_inputs_were_staged()
    {
        var inputs = StageUpstream("staged-res-1", "a", """{"upstream":{},"coverage":[{"segmentId":"s-1","state":"accepted"}]}""");
        try
        {
            var manifest = Write(SimulatedResultModes.IgnoreInputs, out _, inputs);

            IsSchemaValid().ShouldBeTrue();
            manifest["upstreamLineage"]!.AsArray().Count.ShouldBe(0);
        }
        finally
        {
            Directory.Delete(inputs, true);
        }
    }
}
