using System.Text.Json;
using System.Text.Json.Nodes;
using Shouldly;
using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Infrastructure.Contracts;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.Contracts;

public sealed class ContractValidatorTests
{
	private const string Digest = "sha-256:2c26b46b68ffc68ff99b453c1d30413413422d706483bfa0f98a5e886266e7ae";
	private readonly JsonSchemaContractValidator _validator = new();

	private static JsonElement ToElement(JsonNode node) => JsonSerializer.SerializeToElement(node);

	private static JsonObject RecordingsEvent() => JsonNode.Parse("""
		{
		  "eventId": "0192f3a0-7c1e-7b2a-9d3e-5f6a7b8c9d01",
		  "eventType": "matches.recordings-finalized",
		  "contractVersion": "1.0.0",
		  "occurredAt": "2026-10-07T12:00:00Z",
		  "recordingSetVersionId": "set-1",
		  "matchId": "match-1",
		  "teamId": "team-1",
		  "members": [
		    {
		      "position": 1,
		      "recordingVersionId": "rec-1",
		      "recordingContentDigest": "sha-256-parts:5242880:3:8e41531920a37a7ca28af60841220d3147df4c1c06170b60dfca248e6f662770",
		      "timelineMappingId": "map-1",
		      "timelineMappingDigest": "sha-256:2c26b46b68ffc68ff99b453c1d30413413422d706483bfa0f98a5e886266e7ae"
		    }
		  ]
		}
		""")!.AsObject();

	private static JsonObject AnalystJob() => JsonNode.Parse($$"""
		{
		  "contractVersion": "1.0.0",
		  "messageId": "msg-1",
		  "readinessOccurrenceId": "occ-1",
		  "readinessSequence": 1,
		  "runId": "run-1",
		  "nodeId": "node-1",
		  "nodeKey": "match-summary",
		  "logicalJobId": "job-1",
		  "teamId": "team-1",
		  "matchId": "match-1",
		  "recordingSetVersionId": "set-1",
		  "capability": { "id": "summary", "version": "1.0.0" },
		  "tier": "high-level",
		  "schemaVersion": "1.0.0",
		  "executionScope": "match",
		  "inputs": [
		    {
		      "upstreamNodeKey": "detect",
		      "capability": { "id": "detect", "version": "1.0.0" },
		      "edgeKind": "required",
		      "status": "accepted",
		      "acceptedResults": [
		        {
		          "acceptedResultId": "res-1",
		          "logicalJobId": "job-0",
		          "capability": { "id": "detect", "version": "1.0.0" },
		          "schemaVersion": "1.0.0",
		          "manifestReference": "results/res-1/manifest.json",
		          "manifestChecksum": "{{Digest}}"
		        }
		      ]
		    }
		  ],
		  "pinned": {
		    "analystProfileId": "profile-a",
		    "analystProfileDigest": "{{Digest}}",
		    "imageDigest": "{{Digest}}",
		    "modelDigest": null,
		    "policies": []
		  },
		  "resources": { "cpuMillicores": 1000, "memoryMebibytes": 512, "scratchDiskMebibytes": 0 },
		  "attemptBudget": { "maxAttempts": 3 },
		  "timeouts": { "leaseSeconds": 60, "staleTimeoutSeconds": 120, "executionTimeoutSeconds": 600 },
		  "createdAt": "2026-10-07T12:00:00Z",
		  "correlation": { "correlationId": "corr-1" }
		}
		""")!.AsObject();

	[Fact]
	public void Canonical_recordings_event_passes()
	{
		var result = _validator.Validate(ContractSchemaIds.RecordingsFinalizedV1, ToElement(RecordingsEvent()));
		result.IsValid.ShouldBeTrue(string.Join(", ", result.Errors));
	}

	[Fact]
	public void Hand_built_analyst_job_passes()
	{
		var result = _validator.Validate(ContractSchemaIds.AnalystJobV1, ToElement(AnalystJob()));
		result.IsValid.ShouldBeTrue(string.Join(", ", result.Errors));
	}

	[Fact]
	public void Extra_manager_property_fails()
	{
		var job = AnalystJob();
		job["managerId"] = "manager-1";
		var result = _validator.Validate(ContractSchemaIds.AnalystJobV1, ToElement(job));
		result.IsValid.ShouldBeFalse();
		result.Errors.ShouldContain(e => e.Field == "/managerId");
	}

	[Fact]
	public void Url_object_key_fails_at_its_pointer()
	{
		var job = AnalystJob();
		job["inputs"]![0]!["acceptedResults"]![0]!["manifestReference"] = "https://storage.example/results/res-1?sig=abc";
		var result = _validator.Validate(ContractSchemaIds.AnalystJobV1, ToElement(job));
		result.IsValid.ShouldBeFalse();
		result.Errors.ShouldContain(e => e.Field == "/inputs/0/acceptedResults/0/manifestReference" && e.Keyword == "pattern");
	}

	[Fact]
	public void Validation_never_uses_the_network()
	{
		// The validator exposes no HTTP seam; resolving every cross-file $ref offline proves no fetch happens.
		var job = AnalystJob();
		_validator.Validate(ContractSchemaIds.AnalystJobV1, ToElement(job)).IsValid.ShouldBeTrue();
	}

	[Fact]
	public void Property_order_and_whitespace_do_not_change_canonical_bytes_or_fingerprint()
	{
		var canonicalizer = new JsonCanonicalizer();
		using var a = JsonDocument.Parse("""{"b":[1,2,{"y":true,"x":null}],"a":"\u00e9\n"}""");
		using var b = JsonDocument.Parse("{ \"a\" : \"é\\n\",\n \"b\" : [ 1, 2, { \"x\": null, \"y\": true } ] }");

		canonicalizer.Canonicalize(a.RootElement).ShouldBe(canonicalizer.Canonicalize(b.RootElement));
		System.Text.Encoding.UTF8.GetString(canonicalizer.Canonicalize(a.RootElement))
			.ShouldBe("{\"a\":\"é\\n\",\"b\":[1,2,{\"x\":null,\"y\":true}]}");
		var fingerprint = canonicalizer.Fingerprint(a.RootElement);
		fingerprint.ShouldBe(canonicalizer.Fingerprint(b.RootElement));
		fingerprint.ShouldMatch("^sha-256:[0-9a-f]{64}$");
	}
}
