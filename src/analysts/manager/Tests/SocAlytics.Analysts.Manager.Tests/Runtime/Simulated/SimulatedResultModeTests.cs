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

    private JsonNode Write(string? mode, out AnalystJobMessage job)
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
                new AttemptResources(1, 1, []), job, 1, _directory),
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
}
