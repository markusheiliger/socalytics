using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using SocAlytics.Platform.Application.Abstractions.ObjectStorage;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Application.Segments;
using SocAlytics.Platform.Domain.Recordings;
using SocAlytics.Platform.Domain.Segments;
using SocAlytics.Platform.Integration.Tests.Analysis.Evidence.Support;
using SocAlytics.Platform.Integration.Tests.IdentityAccess.Support;
using SocAlytics.Platform.Integration.Tests.Infrastructure;
using SocAlytics.Platform.Integration.Tests.Recordings;
using SocAlytics.Platform.Integration.Tests.Segments;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.Analysis.Evidence;

// SC-004; FR-007 to FR-011: explicit part states and the demand they record, over HTTP against PostgreSQL and RustFS.
public sealed class EvidencePartStateTests(PostgresContainerFixture postgres, RustFsContainerFixture rustFs)
{
	private static readonly Dictionary<string, string?> ShortMaxDuration = new() { ["Analysis:Evidence:MaxDuration"] = "00:00:03" };

	private sealed class SwitchableStorage(IObjectStorage inner) : DelegatingStorage(inner)
	{
		public volatile bool ChecksumUnavailable;

		public override Task<ObjectChecksum?> GetObjectChecksumAsync(string objectKey, CancellationToken cancellationToken) =>
			ChecksumUnavailable ? throw new ObjectStorageUnavailableException() : base.GetObjectChecksumAsync(objectKey, cancellationToken);
	}

	private static Task<EvidenceScenario> StartAsync(PostgresContainerFixture postgres, RustFsContainerFixture rustFs, CancellationToken ct) =>
		EvidenceScenario.StartAsync(
			postgres,
			rustFs,
			ct,
			extraSettings: ShortMaxDuration,
			configureServices: services =>
			{
				var descriptor = services.Last(d => d.ServiceType == typeof(IObjectStorage));
				services.Remove(descriptor);
				services.AddSingleton<IObjectStorage>(sp =>
					new SwitchableStorage((IObjectStorage)ActivatorUtilities.CreateInstance(sp, descriptor.ImplementationType!)));
			});

	private static SwitchableStorage Storage(EvidenceScenario s) => (SwitchableStorage)s.Factory.Services.GetRequiredService<IObjectStorage>();

	private static async Task<(HttpStatusCode Status, JsonElement Body)> ResolveAsync(
		EvidenceScenario s, ApiSession session, object body, CancellationToken ct)
	{
		using var response = await session.SendAsync(HttpMethod.Post, s.EvidencePath, body, ct);
		var text = await response.Content.ReadAsStringAsync(ct);
		return (response.StatusCode, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone());
	}

	private static object Fact(EvidenceScenario s, int ordinal, bool retryFailed = false) =>
		new { fact = new { acceptedResultId = s.ResultId, ordinal }, retryFailed };

	private static async Task<JsonElement[]> PartsAsync(EvidenceScenario s, object body, CancellationToken ct)
	{
		var (status, resolution) = await ResolveAsync(s, s.Coach, body, ct);
		status.ShouldBe(HttpStatusCode.OK, resolution.ToString());
		return [.. resolution.GetProperty("parts").EnumerateArray()];
	}

	private static string StateOf(JsonElement part) => part.GetProperty("state").GetString()!;

	private static async Task<(Guid Id, string State, string? Digest, string? MediaKey, string? ManifestKey)> SegmentAsync(
		EvidenceScenario s, int segmentNumber, CancellationToken ct)
	{
		await using var connection = new NpgsqlConnection(s.Setup.Db.MigratorConnectionString);
		await connection.OpenAsync(ct);
		return await connection.QuerySingleAsync<(Guid, string, string?, string?, string?)>(new CommandDefinition(
			"SELECT id, state, media_digest, media_object_key, manifest_object_key FROM socalytics.materialized_segments WHERE segment_number = @segmentNumber",
			new { segmentNumber }, cancellationToken: ct));
	}

	private static async Task<string?> FailureReasonAsync(EvidenceScenario s, int segmentNumber, CancellationToken ct)
	{
		await using var connection = new NpgsqlConnection(s.Setup.Db.MigratorConnectionString);
		await connection.OpenAsync(ct);
		return await connection.QuerySingleAsync<string?>(new CommandDefinition(
			"SELECT failure_reason FROM socalytics.materialized_segments WHERE segment_number = @segmentNumber",
			new { segmentNumber }, cancellationToken: ct));
	}

	private static async Task DeleteAllSegmentRowsAsync(EvidenceScenario s, CancellationToken ct)
	{
		await using var connection = new NpgsqlConnection(s.Setup.Db.MigratorConnectionString);
		await connection.OpenAsync(ct);
		await connection.ExecuteAsync(new CommandDefinition("DELETE FROM socalytics.materialized_segments", cancellationToken: ct));
	}

	private static async Task InScopeAsync(EvidenceScenario s, Func<ISegmentStore, Task> action, CancellationToken ct)
	{
		await using var scope = s.Factory.Services.CreateAsyncScope();
		await using var uow = await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().BeginAsync(ct);
		await action(scope.ServiceProvider.GetRequiredService<ISegmentStore>());
		await uow.CommitAsync(ct);
	}

	[Fact]
	public async Task Never_materialized_segment_is_materializing_then_available_with_a_link_after_the_worker_ran()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var s = await StartAsync(postgres, rustFs, ct);
		await DeleteAllSegmentRowsAsync(s, ct);

		var first = await PartsAsync(s, Fact(s, EvidenceScenario.InsideFactOrdinal), ct);
		var preparing = first.ShouldHaveSingleItem();
		StateOf(preparing).ShouldBe("materializing");
		preparing.GetProperty("retryAfterSeconds").GetInt32().ShouldBeGreaterThan(0);
		preparing.TryGetProperty("media", out _).ShouldBeFalse();
		(await s.ScalarAsync("SELECT count(*) FROM socalytics.materialized_segments WHERE demanded", ct)).ShouldBe(1);

		await s.MaterializeSegmentsAsync(ct);

		var second = (await PartsAsync(s, Fact(s, EvidenceScenario.InsideFactOrdinal), ct)).ShouldHaveSingleItem();
		StateOf(second).ShouldBe("available");
		second.GetProperty("media").GetProperty("url").GetString().ShouldNotBeNullOrEmpty();
	}

	[Fact]
	public async Task Purged_segment_is_rematerializing_beside_available_parts_until_the_worker_ran()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var s = await StartAsync(postgres, rustFs, ct);
		await s.MaterializeAllAsync(ct);
		var second = await SegmentAsync(s, 2, ct);
		await InScopeAsync(s, async store => (await store.TryPurgeAsync(second.Id, ct)).ShouldBeTrue(), ct);

		var mixed = await PartsAsync(s, Fact(s, EvidenceScenario.BoundaryFactOrdinal), ct);
		mixed.Select(StateOf).ShouldBe(["available", "rematerializing"]);
		mixed[0].GetProperty("media").GetProperty("url").GetString().ShouldNotBeNullOrEmpty();
		mixed[1].GetProperty("retryAfterSeconds").GetInt32().ShouldBeGreaterThan(0);
		mixed[1].TryGetProperty("media", out _).ShouldBeFalse();

		// A repeated resolution reports the state without a new request.
		(await PartsAsync(s, Fact(s, EvidenceScenario.BoundaryFactOrdinal), ct)).Select(StateOf).ShouldBe(["available", "rematerializing"]);

		await s.MaterializeSegmentsAsync(ct);
		(await PartsAsync(s, Fact(s, EvidenceScenario.BoundaryFactOrdinal), ct)).Select(StateOf).ShouldBe(["available", "available"]);
	}

	[Fact]
	public async Task Failed_segment_is_reported_with_its_reason_and_requested_again_only_with_retry_failed()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var s = await StartAsync(postgres, rustFs, ct);
		await s.MaterializeAllAsync(ct);
		var first = await SegmentAsync(s, 1, ct);
		await InScopeAsync(
			s,
			async store => (await store.TryInvalidateAvailableAsync(first.Id, first.Digest!, SegmentFailureReasons.ExtractionFailed, ct)).ShouldNotBeNull(),
			ct);

		var without = (await PartsAsync(s, Fact(s, EvidenceScenario.InsideFactOrdinal), ct)).ShouldHaveSingleItem();
		StateOf(without).ShouldBe("failed");
		without.GetProperty("reason").GetString().ShouldBe(SegmentFailureReasons.ExtractionFailed);
		without.TryGetProperty("media", out _).ShouldBeFalse();
		(await SegmentAsync(s, 1, ct)).State.ShouldBe("failed");

		var retried = (await PartsAsync(s, Fact(s, EvidenceScenario.InsideFactOrdinal, retryFailed: true), ct)).ShouldHaveSingleItem();
		StateOf(retried).ShouldBe("materializing");
		(await SegmentAsync(s, 1, ct)).State.ShouldBe("absent");
		(await s.ScalarAsync("SELECT count(*) FROM socalytics.materialized_segments WHERE segment_number = 1 AND demanded", ct)).ShouldBe(1);

		await s.MaterializeSegmentsAsync(ct);
		StateOf((await PartsAsync(s, Fact(s, EvidenceScenario.InsideFactOrdinal), ct)).ShouldHaveSingleItem()).ShouldBe("available");
	}

	[Fact]
	public async Task Uncovered_part_is_not_covered_and_records_no_demand()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var s = await StartAsync(postgres, rustFs, ct);
		await s.MaterializeAllAsync(ct);
		await DeleteAllSegmentRowsAsync(s, ct);

		// The recording ends at 10 s, so the part from 10 s to 12 s has no media.
		var parts = await PartsAsync(s, new { range = new { fromMs = 9_000, toMs = 12_000 } }, ct);
		parts.Select(StateOf).ShouldBe(["materializing", "not-covered"]);
		parts[1].TryGetProperty("media", out _).ShouldBeFalse();
		parts[1].TryGetProperty("retryAfterSeconds", out _).ShouldBeFalse();
		(await s.ScalarAsync("SELECT count(*) FROM socalytics.materialized_segments", ct)).ShouldBe(1);
	}

	[Fact]
	public async Task Altered_stored_bytes_are_refused_the_segment_fails_and_its_objects_are_deleted()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var s = await StartAsync(postgres, rustFs, ct);
		await s.MaterializeAllAsync(ct);
		var segment = await SegmentAsync(s, 1, ct);
		var storage = Storage(s);
		var altered = RandomNumberGenerator.GetBytes(64);
		await storage.PutObjectAsync(
			segment.MediaKey!, "video/mp4", new MemoryStream(altered), altered.Length, Sha256Digest.FromBytes(SHA256.HashData(altered)), ct);

		var (status, resolution) = await ResolveAsync(s, s.Coach, Fact(s, EvidenceScenario.InsideFactOrdinal), ct);

		status.ShouldBe(HttpStatusCode.OK, resolution.ToString());
		var part = resolution.GetProperty("parts").EnumerateArray().ShouldHaveSingleItem();
		StateOf(part).ShouldBe("failed");
		part.GetProperty("reason").GetString().ShouldBe(SegmentFailureReasons.MediaInvalid);
		part.TryGetProperty("media", out _).ShouldBeFalse();
		(await SegmentAsync(s, 1, ct)).State.ShouldBe("failed");
		(await FailureReasonAsync(s, 1, ct)).ShouldBe(SegmentFailureReasons.MediaInvalid);
		(await storage.GetObjectChecksumAsync(segment.MediaKey!, ct)).ShouldBeNull();
		(await storage.GetObjectChecksumAsync(segment.ManifestKey!, ct)).ShouldBeNull();
	}

	[Fact]
	public async Task Two_members_resolving_the_same_absent_segment_concurrently_end_with_one_association()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var s = await StartAsync(postgres, rustFs, ct);
		await DeleteAllSegmentRowsAsync(s, ct);

		var results = await Task.WhenAll(
			ResolveAsync(s, s.Coach, Fact(s, EvidenceScenario.InsideFactOrdinal), ct),
			ResolveAsync(s, s.Admin, Fact(s, EvidenceScenario.InsideFactOrdinal), ct));

		foreach (var (status, body) in results)
		{
			status.ShouldBe(HttpStatusCode.OK, body.ToString());
			StateOf(body.GetProperty("parts").EnumerateArray().Single()).ShouldBe("materializing");
		}

		(await s.ScalarAsync("SELECT count(*) FROM socalytics.materialized_segments", ct)).ShouldBe(1);
		await s.MaterializeSegmentsAsync(ct);
		(await s.ScalarAsync("SELECT count(*) FROM socalytics.materialized_segments WHERE state = 'available'", ct)).ShouldBe(1);

		var digests = new List<string>();
		foreach (var session in new[] { s.Coach, s.Admin })
		{
			var (status, body) = await ResolveAsync(s, session, Fact(s, EvidenceScenario.InsideFactOrdinal), ct);
			status.ShouldBe(HttpStatusCode.OK);
			digests.Add(body.GetProperty("parts").EnumerateArray().Single().GetProperty("media").GetProperty("mediaDigest").GetString()!);
		}

		digests.Distinct().Count().ShouldBe(1);
	}

	[Fact]
	public async Task Unavailable_storage_gives_503_without_links()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var s = await StartAsync(postgres, rustFs, ct);
		await s.MaterializeAllAsync(ct);
		Storage(s).ChecksumUnavailable = true;

		using var response = await s.Coach.SendAsync(HttpMethod.Post, s.EvidencePath, Fact(s, EvidenceScenario.BoundaryFactOrdinal), ct);
		var text = await response.Content.ReadAsStringAsync(ct);

		response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable, text);
		JsonDocument.Parse(text).RootElement.GetProperty("code").GetString().ShouldBe("evidence-storage-unavailable");
		text.ShouldNotContain("\"url\"");
		text.ShouldNotContain("X-Amz-Signature");
		(await s.ScalarAsync("SELECT count(*) FROM socalytics.security_audit_event WHERE event_type = 'analysis.evidence-media.granted'", ct)).ShouldBe(0);
		(await SegmentAsync(s, 1, ct)).State.ShouldBe("available");
	}
}
