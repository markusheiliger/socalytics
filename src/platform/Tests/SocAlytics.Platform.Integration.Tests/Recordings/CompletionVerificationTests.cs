using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using SocAlytics.Platform.Application.Abstractions.ObjectStorage;
using SocAlytics.Platform.Domain.Recordings;
using SocAlytics.Platform.Integration.Tests.IdentityAccess.Support;
using SocAlytics.Platform.Integration.Tests.Infrastructure;
using SocAlytics.Platform.Integration.Tests.Recordings.Support;
using SocAlytics.Platform.Migrator;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.Recordings;

public sealed class CompletionVerificationTests(PostgresContainerFixture postgres, RustFsContainerFixture rustFs)
{
	private sealed class Faults
	{
		public Func<CompositeIntegrityEvidence?, CompositeIntegrityEvidence?>? Evidence { get; set; }

		public bool ThrowAfterComplete { get; set; }
	}

	private sealed class FaultingObjectStorage(IObjectStorage inner, Faults faults) : IObjectStorage
	{
		public Task<MultipartUploadReference> InitiateCompositeMultipartUploadAsync(string objectKey, string contentType, CancellationToken cancellationToken) =>
			inner.InitiateCompositeMultipartUploadAsync(objectKey, contentType, cancellationToken);

		public PartUploadGrant PresignUploadPart(MultipartUploadReference upload, int partNumber, long contentLength, Sha256Digest partDigest, DateTimeOffset expiresAt) =>
			inner.PresignUploadPart(upload, partNumber, contentLength, partDigest, expiresAt);

		public Task<IReadOnlyList<StoredPart>> ListPartsAsync(MultipartUploadReference upload, int? maxParts, CancellationToken cancellationToken) =>
			inner.ListPartsAsync(upload, maxParts, cancellationToken);

		public async Task CompleteMultipartUploadAsync(
			MultipartUploadReference upload, IReadOnlyList<CompletedPartEntry> parts, CompositeContentDigest contentDigest, long totalSizeBytes, CancellationToken cancellationToken)
		{
			await inner.CompleteMultipartUploadAsync(upload, parts, contentDigest, totalSizeBytes, cancellationToken);
			if (faults.ThrowAfterComplete)
			{
				faults.ThrowAfterComplete = false;
				throw new InvalidOperationException("Injected interruption after the multipart completion.");
			}
		}

		public async Task<CompositeIntegrityEvidence?> GetIntegrityEvidenceAsync(string objectKey, CancellationToken cancellationToken)
		{
			var evidence = await inner.GetIntegrityEvidenceAsync(objectKey, cancellationToken);
			return faults.Evidence is null ? evidence : faults.Evidence(evidence);
		}

		public Task AbortMultipartUploadAsync(MultipartUploadReference upload, CancellationToken cancellationToken) =>
			inner.AbortMultipartUploadAsync(upload, cancellationToken);

		public Task DeleteObjectAsync(string objectKey, CancellationToken cancellationToken) =>
			inner.DeleteObjectAsync(objectKey, cancellationToken);
	}

	private sealed record Setup(IsolatedDatabase Db, PlatformApiFactory Factory, ApiSession Coach, ClubHierarchy Hierarchy, Faults Faults);

	private static readonly object ValidMapping = new { timelineMapping = new { spans = new[] { new { mediaStartSeconds = 0, mediaEndSeconds = 10, matchStartSeconds = 5 } } } };

	private async Task<Setup> StartAsync(CancellationToken ct)
	{
		var db = await postgres.CreateDatabaseAsync(ct);
		(await MigratorHarness.RunAsync(db, TestMigrationCatalogs.Platform(), ct)).ExitCode.ShouldBe(MigratorExitCode.Success);
		var faults = new Faults();
		var factory = new PlatformApiFactory(
			db,
			objectStorage: new Dictionary<string, string?>
			{
				["ServiceUrl"] = rustFs.ServiceUrl,
				["Region"] = rustFs.Region,
				["AccessKey"] = rustFs.AccessKey,
				["SecretKey"] = rustFs.SecretKey,
				["Bucket"] = rustFs.Bucket,
			},
			configureServices: services =>
			{
				var descriptor = services.Last(d => d.ServiceType == typeof(IObjectStorage));
				services.Remove(descriptor);
				services.AddSingleton<IObjectStorage>(sp =>
					new FaultingObjectStorage((IObjectStorage)ActivatorUtilities.CreateInstance(sp, descriptor.ImplementationType!), faults));
			});
		factory.Time.Set(DateTimeOffset.UtcNow);
		await TestMembers.SeedAsync(db, "admin-1", clubRoles: ["club-admin"], cancellationToken: ct);
		var coachId = await TestMembers.SeedAsync(db, "coach-1", cancellationToken: ct);
		var admin = await ApiSession.SignInAsync(factory, "admin-1", TestMembers.DefaultPassword, ct);
		var hierarchy = await new ClubHierarchyBuilder(factory, admin).CreateAsync(ct);
		await ExecuteAsync(
			db,
			$"INSERT INTO socalytics.team_role_assignment (member_account_id, team_id, role, assigned_at, assigned_by_account_id) VALUES ('{coachId}', '{hierarchy.TeamId}', 'coach', now(), '{coachId}')",
			ct);
		var coach = await ApiSession.SignInAsync(factory, "coach-1", TestMembers.DefaultPassword, ct);
		return new Setup(db, factory, coach, hierarchy, faults);
	}

	private static async Task<long> ExecuteAsync(IsolatedDatabase db, string sql, CancellationToken ct)
	{
		await using var connection = new NpgsqlConnection(db.MigratorConnectionString);
		await connection.OpenAsync(ct);
		await using var command = new NpgsqlCommand(sql, connection);
		return Convert.ToInt64(await command.ExecuteScalarAsync(ct) ?? 0L);
	}

	private static async Task<HttpResponseMessage> PostAsync(ApiSession session, string url, object body, CancellationToken ct, string? key = null)
	{
		using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
		request.Headers.Add("X-CSRF-Token", session.AntiforgeryToken);
		request.Headers.Add("Idempotency-Key", key ?? Guid.NewGuid().ToString());
		return await session.Client.SendAsync(request, ct);
	}

	private static Task<HttpResponseMessage> CompleteAsync(Setup s, string id, CancellationToken ct, string? key = null) =>
		PostAsync(s.Coach, $"/api/v1/matches/{s.Hierarchy.MatchId}/upload-sessions/{id}/completion", ValidMapping, ct, key);

	private static async Task AssertConflictAsync(HttpResponseMessage response, string code, CancellationToken ct)
	{
		response.StatusCode.ShouldBe(HttpStatusCode.Conflict, await response.Content.ReadAsStringAsync(ct));
		(await response.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("code").GetString().ShouldBe(code);
	}

	// Starts an upload and writes only the listed part numbers (1-based).
	private static async Task<string> StartAsync(Setup s, TestRecording recording, int[] uploadParts, CancellationToken ct)
	{
		using var response = await PostAsync(s.Coach, $"/api/v1/matches/{s.Hierarchy.MatchId}/upload-sessions", recording.Declaration(), ct);
		response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(ct));
		var body = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
		using var storage = new HttpClient();
		foreach (var grant in body.GetProperty("grants").GetProperty("parts").EnumerateArray())
		{
			var number = grant.GetProperty("partNumber").GetInt32();
			if (uploadParts.Contains(number))
			{
				using var put = await RecordingTestData.PutPartAsync(storage, grant, recording.Parts[number - 1], ct);
				put.IsSuccessStatusCode.ShouldBeTrue(await put.Content.ReadAsStringAsync(ct));
			}
		}

		return body.GetProperty("session").GetProperty("id").GetString()!;
	}

	private static async Task AssertNothingCommittedAsync(Setup s, CancellationToken ct)
	{
		(await ExecuteAsync(s.Db, "SELECT count(*) FROM socalytics.recording_versions", ct)).ShouldBe(0);
		(await ExecuteAsync(s.Db, "SELECT count(*) FROM socalytics.recording_timeline_mappings", ct)).ShouldBe(0);
		(await ExecuteAsync(s.Db, "SELECT count(*) FROM socalytics.recording_upload_sessions WHERE state = 'pending'", ct)).ShouldBe(1);
	}

	[Fact]
	// US1 AS7, FR-009
	public async Task CompletedSessionPresignsNoFurtherGrants()
	{
		var ct = TestContext.Current.CancellationToken;
		var s = await StartAsync(ct);
		await using var db0 = s.Db;
		await using var fac0 = s.Factory;
		var id = await StartAsync(s, RecordingTestData.Generate(1, 1024), [1], ct);
		using var completed = await CompleteAsync(s, id, ct);
		completed.StatusCode.ShouldBe(HttpStatusCode.Created, await completed.Content.ReadAsStringAsync(ct));

		using var grants = await PostAsync(s.Coach, $"/api/v1/matches/{s.Hierarchy.MatchId}/upload-sessions/{id}/grants", new { partNumbers = new[] { 1 } }, ct);
		await AssertConflictAsync(grants, "upload-session-completed", ct);
		(await grants.Content.ReadAsStringAsync(ct)).ShouldNotContain("X-Amz-Signature");
	}

	[Fact]
	// FR-014, R6
	public async Task MissingPartIsRejectedUntilUploadedWithFreshGrant()
	{
		var ct = TestContext.Current.CancellationToken;
		var s = await StartAsync(ct);
		await using var db0 = s.Db;
		await using var fac0 = s.Factory;
		var recording = RecordingTestData.Generate(2, 5_242_880, 100);
		var id = await StartAsync(s, recording, [1], ct);

		using var incomplete = await CompleteAsync(s, id, ct);
		await AssertConflictAsync(incomplete, "upload-parts-incomplete", ct);
		await AssertNothingCommittedAsync(s, ct);

		using var grants = await PostAsync(s.Coach, $"/api/v1/matches/{s.Hierarchy.MatchId}/upload-sessions/{id}/grants", new { partNumbers = new[] { 2 } }, ct);
		grants.StatusCode.ShouldBe(HttpStatusCode.OK, await grants.Content.ReadAsStringAsync(ct));
		var grant = (await grants.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("parts")[0];
		using var storage = new HttpClient();
		using var put = await RecordingTestData.PutPartAsync(storage, grant, recording.Parts[1], ct);
		put.IsSuccessStatusCode.ShouldBeTrue(await put.Content.ReadAsStringAsync(ct));

		using var completed = await CompleteAsync(s, id, ct);
		completed.StatusCode.ShouldBe(HttpStatusCode.Created, await completed.Content.ReadAsStringAsync(ct));
	}

	[Fact]
	public async Task PartStoredWithAnotherSizeIsMismatchAndSessionStaysPending()
	{
		var ct = TestContext.Current.CancellationToken;
		var s = await StartAsync(ct);
		await using var db0 = s.Db;
		await using var fac0 = s.Factory;
		var recording = RecordingTestData.Generate(3, 5_242_880, 100);
		var id = await StartAsync(s, recording, [1, 2], ct);

		// The stored last part has 100 bytes, the session now declares 101.
		await ExecuteAsync(s.Db, $"UPDATE socalytics.recording_upload_sessions SET total_size_bytes = total_size_bytes + 1 WHERE id = '{id}' RETURNING 1", ct);
		using var response = await CompleteAsync(s, id, ct);
		await AssertConflictAsync(response, "upload-part-mismatch", ct);
		await AssertNothingCommittedAsync(s, ct);
	}

	[Fact]
	public async Task UnverifiableOrDifferentAssembledObjectIsRejected()
	{
		var ct = TestContext.Current.CancellationToken;
		var s = await StartAsync(ct);
		await using var db0 = s.Db;
		await using var fac0 = s.Factory;
		var id = await StartAsync(s, RecordingTestData.Generate(4, 1024), [1], ct);

		s.Faults.Evidence = e => e! with { Checksum = null };
		using (var missing = await CompleteAsync(s, id, ct))
		{
			await AssertConflictAsync(missing, "integrity-evidence-unavailable", ct);
		}

		await AssertNothingCommittedAsync(s, ct);

		var cases = new Func<CompositeIntegrityEvidence?, CompositeIntegrityEvidence?>[]
		{
			e => e! with { Checksum = Convert.ToBase64String(new byte[32]) + "-1" },
			e => e! with { ChecksumType = "FULL_OBJECT" },
			e => e! with { ContentLength = e.ContentLength + 1 },
		};
		foreach (var fault in cases)
		{
			s.Faults.Evidence = fault;
			using var response = await CompleteAsync(s, id, ct);
			await AssertConflictAsync(response, "upload-object-mismatch", ct);
			await AssertNothingCommittedAsync(s, ct);
		}
	}

	[Fact]
	// R6 recovery
	public async Task InterruptedCommitIsRecoveredByRetryWithSameKey()
	{
		var ct = TestContext.Current.CancellationToken;
		var s = await StartAsync(ct);
		await using var db0 = s.Db;
		await using var fac0 = s.Factory;
		var id = await StartAsync(s, RecordingTestData.Generate(5, 1024), [1], ct);
		var key = Guid.NewGuid().ToString();

		s.Faults.ThrowAfterComplete = true;
		try
		{
			using var interrupted = await CompleteAsync(s, id, ct, key);
			interrupted.IsSuccessStatusCode.ShouldBeFalse();
		}
		catch (InvalidOperationException)
		{
		}

		s.Faults.ThrowAfterComplete.ShouldBeFalse();
		await AssertNothingCommittedAsync(s, ct);

		using var retry = await CompleteAsync(s, id, ct, key);
		retry.StatusCode.ShouldBe(HttpStatusCode.Created, await retry.Content.ReadAsStringAsync(ct));
		(await ExecuteAsync(s.Db, "SELECT count(*) FROM socalytics.recording_versions", ct)).ShouldBe(1);
	}
}
