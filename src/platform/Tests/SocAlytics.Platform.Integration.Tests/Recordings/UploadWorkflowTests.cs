using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Amazon.S3.Model;
using Npgsql;
using Shouldly;
using SocAlytics.Platform.Integration.Tests.IdentityAccess.Support;
using SocAlytics.Platform.Integration.Tests.Infrastructure;
using SocAlytics.Platform.Integration.Tests.Recordings.Support;
using SocAlytics.Platform.Migrator;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.Recordings;

public sealed class UploadWorkflowTests(PostgresContainerFixture postgres, RustFsContainerFixture rustFs)
{
	private sealed record Setup(IsolatedDatabase Db, PlatformApiFactory Factory, ApiSession Admin, ApiSession Coach, Guid CoachId, ClubHierarchy Hierarchy);

	private async Task<Setup> StartAsync(CancellationToken ct)
	{
		var db = await postgres.CreateDatabaseAsync(ct);
		(await MigratorHarness.RunAsync(db, TestMigrationCatalogs.Platform(), ct)).ExitCode.ShouldBe(MigratorExitCode.Success);
		var factory = new PlatformApiFactory(db, objectStorage: new Dictionary<string, string?>
		{
			["ServiceUrl"] = rustFs.ServiceUrl,
			["Region"] = rustFs.Region,
			["AccessKey"] = rustFs.AccessKey,
			["SecretKey"] = rustFs.SecretKey,
			["Bucket"] = rustFs.Bucket,
		});
		factory.Time.Set(DateTimeOffset.UtcNow);
		await TestMembers.SeedAsync(db, "admin-1", clubRoles: ["club-admin"], cancellationToken: ct);
		var coachId = await TestMembers.SeedAsync(db, "coach-1", cancellationToken: ct);
		var admin = await ApiSession.SignInAsync(factory, "admin-1", TestMembers.DefaultPassword, ct);
		var hierarchy = await new ClubHierarchyBuilder(factory, admin).CreateAsync(ct);
		await using (var c = new NpgsqlConnection(db.AppConnectionString))
		{
			await c.OpenAsync(ct);
			await using var cmd = new NpgsqlCommand(
				"INSERT INTO socalytics.team_role_assignment (member_account_id, team_id, role, assigned_at, assigned_by_account_id) VALUES (@m, @t, 'coach', now(), @m)", c);
			cmd.Parameters.AddWithValue("m", coachId);
			cmd.Parameters.AddWithValue("t", hierarchy.TeamId);
			await cmd.ExecuteNonQueryAsync(ct);
		}

		var coach = await ApiSession.SignInAsync(factory, "coach-1", TestMembers.DefaultPassword, ct);
		return new Setup(db, factory, admin, coach, coachId, hierarchy);
	}

	private static async Task<HttpResponseMessage> StartUploadAsync(ApiSession session, Guid matchId, object body, string? traceId, CancellationToken ct, string? key = null)
	{
		using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/matches/{matchId}/upload-sessions") { Content = JsonContent.Create(body) };
		request.Headers.Add("X-CSRF-Token", session.AntiforgeryToken);
		request.Headers.Add("Idempotency-Key", key ?? Guid.NewGuid().ToString());
		if (traceId is not null)
		{
			request.Headers.Add("traceparent", $"00-{traceId}-0123456789abcdef-01");
		}

		return await session.Client.SendAsync(request, ct);
	}

	private static async Task<long> ScalarAsync(IsolatedDatabase db, string sql, CancellationToken ct)
	{
		await using var connection = new NpgsqlConnection(db.MigratorConnectionString);
		await connection.OpenAsync(ct);
		await using var command = new NpgsqlCommand(sql, connection);
		return Convert.ToInt64(await command.ExecuteScalarAsync(ct));
	}

	private async Task<int> InFlightUploadsAsync(CancellationToken ct)
	{
		using var s3 = rustFs.CreateS3Client();
		var listed = await s3.ListMultipartUploadsAsync(new ListMultipartUploadsRequest { BucketName = rustFs.Bucket }, ct);
		return listed.MultipartUploads?.Count ?? 0;
	}

	[Fact]
	// US1 AS1, AS3
	public async Task CoachStartsThreePartUploadAndEveryPartCanBeWritten()
	{
		var ct = TestContext.Current.CancellationToken;
		var s = await StartAsync(ct);
		await using var db0 = s.Db;
		await using var fac0 = s.Factory;
		var recording = RecordingTestData.Generate(1, 5_242_880, 5_242_880, 1_234_567);
		var traceId = Guid.NewGuid().ToString("N");

		using var response = await StartUploadAsync(s.Coach, s.Hierarchy.MatchId, recording.Declaration(), traceId, ct);
		response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(ct));
		response.Headers.ETag.ShouldNotBeNull();
		response.Headers.CacheControl!.NoStore.ShouldBeTrue();
		response.Headers.Location.ShouldNotBeNull();
		var body = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
		var session = body.GetProperty("session");
		session.GetProperty("state").GetString().ShouldBe("pending");
		session.GetProperty("contentDigest").GetString().ShouldBe(recording.ExpectedContentDigest);
		session.GetProperty("contentDigest").GetString()!.ShouldStartWith("sha-256-parts:5242880:3:");
		var parts = body.GetProperty("grants").GetProperty("parts");
		parts.GetArrayLength().ShouldBe(3);
		(await ScalarAsync(s.Db, "SELECT count(*) FROM socalytics.recording_versions", ct)).ShouldBe(0);

		using var storage = new HttpClient();
		var i = 0;
		foreach (var grant in parts.EnumerateArray())
		{
			var headers = grant.GetProperty("requiredHeaders");
			headers.TryGetProperty("Content-Length", out _).ShouldBeTrue();
			headers.TryGetProperty("x-amz-checksum-sha256", out _).ShouldBeTrue();
			using var put = await RecordingTestData.PutPartAsync(storage, grant, recording.Parts[i++], ct);
			put.IsSuccessStatusCode.ShouldBeTrue(await put.Content.ReadAsStringAsync(ct));
		}

		var id = session.GetProperty("id").GetString()!;
		using var read = await s.Coach.SendAsync(HttpMethod.Get, $"/api/v1/matches/{s.Hierarchy.MatchId}/upload-sessions/{id}", null, ct);
		read.StatusCode.ShouldBe(HttpStatusCode.OK, string.Join('\n', s.Factory.Logs.Entries.Where(e => e.Exception is not null).Select(e => e.Exception)));
		read.Headers.ETag!.Tag.ShouldBe(response.Headers.ETag!.Tag);
		var read1 = await read.Content.ReadFromJsonAsync<JsonElement>(ct);
		read1.GetProperty("state").GetString().ShouldBe("pending");
		read1.TryGetProperty("grants", out _).ShouldBeFalse();

		await RecordingAuditAssertions.AssertSingleAsync(
			s.Db, "recording.upload.start", id, s.CoachId, s.Hierarchy.TeamId, s.Hierarchy.MatchId, "succeeded", traceId,
			new HashSet<string> { "matchId", "partCount" }, ct);
		await RecordingAuditAssertions.AssertNoGrantUrlAsync(s.Db, ct);
	}

	[Fact]
	public async Task ClubAdminCanStartOnAnyTeamsMatch()
	{
		var ct = TestContext.Current.CancellationToken;
		var s = await StartAsync(ct);
		await using var db0 = s.Db;
		await using var fac0 = s.Factory;
		var recording = RecordingTestData.Generate(2, 1000);

		using var response = await StartUploadAsync(s.Admin, s.Hierarchy.MatchId, recording.Declaration(), null, ct);
		response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(ct));
	}

	[Fact]
	public async Task InvalidDeclarationsAndContentTypesAreRejectedWithoutSideEffects()
	{
		var ct = TestContext.Current.CancellationToken;
		var s = await StartAsync(ct);
		await using var db0 = s.Db;
		await using var fac0 = s.Factory;
		var valid = RecordingTestData.Generate(3, 5_242_880, 100);
		var digests = valid.PartDigests;
		var before = await InFlightUploadsAsync(ct);

		object Decl(long total, long part, string[] d) => new
		{
			recording = new { displayName = "x", contentType = "video/mp4" },
			totalSizeBytes = total,
			partSizeBytes = part,
			partDigests = d,
		};

		var invalid = new[]
		{
			Decl(5_242_980, 5_242_880, [digests[0]]),
			Decl(5_242_980, 1000, digests),
			Decl(0, 5_242_880, digests),
			Decl(5_242_980, 5_242_880, [digests[0], "not-a-digest"]),
		};
		foreach (var declaration in invalid)
		{
			using var response = await StartUploadAsync(s.Coach, s.Hierarchy.MatchId, declaration, null, ct);
			response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, await response.Content.ReadAsStringAsync(ct));
			(await response.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("code").GetString().ShouldBe("upload-declaration-invalid");
		}

		using var disallowed = await StartUploadAsync(s.Coach, s.Hierarchy.MatchId, valid.Declaration("text/plain"), null, ct);
		disallowed.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
		(await disallowed.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("code").GetString().ShouldBe("content-type-not-allowed");

		(await ScalarAsync(s.Db, "SELECT count(*) FROM socalytics.recording_upload_sessions", ct)).ShouldBe(0);
		(await InFlightUploadsAsync(ct)).ShouldBe(before);
	}

	[Fact]
	public async Task MissingIdempotencyKeyAndOversizedBodyAreRefused()
	{
		var ct = TestContext.Current.CancellationToken;
		var s = await StartAsync(ct);
		await using var db0 = s.Db;
		await using var fac0 = s.Factory;

		using var missing = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/matches/{s.Hierarchy.MatchId}/upload-sessions")
		{
			Content = JsonContent.Create(RecordingTestData.Generate(4, 10).Declaration()),
		};
		missing.Headers.Add("X-CSRF-Token", s.Coach.AntiforgeryToken);
		using var missingResponse = await s.Coach.Client.SendAsync(missing, ct);
		missingResponse.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
		(await missingResponse.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("code").GetString().ShouldBe("idempotency-key-missing");

		var oversized = new
		{
			recording = new { displayName = new string('a', 1_100_000), contentType = "video/mp4" },
			totalSizeBytes = 10,
			partSizeBytes = 10,
			partDigests = RecordingTestData.Generate(4, 10).PartDigests,
		};
		using var large = await StartUploadAsync(s.Coach, s.Hierarchy.MatchId, oversized, null, ct);
		large.StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge, await large.Content.ReadAsStringAsync(ct));
		(await ScalarAsync(s.Db, "SELECT count(*) FROM socalytics.recording_upload_sessions", ct)).ShouldBe(0);
	}
}
