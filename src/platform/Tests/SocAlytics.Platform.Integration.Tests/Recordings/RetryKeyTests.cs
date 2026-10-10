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

public sealed class RetryKeyTests(PostgresContainerFixture postgres, RustFsContainerFixture rustFs)
{
	private static readonly object ValidMapping = new { timelineMapping = new { spans = new[] { new { mediaStartSeconds = 0, mediaEndSeconds = 10, matchStartSeconds = 5 } } } };

	private sealed record Setup(IsolatedDatabase Db, PlatformApiFactory Factory, ApiSession Admin, ApiSession Coach, Guid CoachId, ClubHierarchy Hierarchy);

	private async Task<Setup> StartAsync(CancellationToken ct, Dictionary<string, string?>? recordingUpload = null)
	{
		var db = await postgres.CreateDatabaseAsync(ct);
		(await MigratorHarness.RunAsync(db, TestMigrationCatalogs.Platform(), ct)).ExitCode.ShouldBe(MigratorExitCode.Success);
		var factory = new PlatformApiFactory(
			db,
			recordingUpload: recordingUpload,
			objectStorage: new Dictionary<string, string?>
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

	private static async Task<HttpResponseMessage> PostAsync(ApiSession session, string url, object body, string key, CancellationToken ct)
	{
		using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
		request.Headers.Add("X-CSRF-Token", session.AntiforgeryToken);
		request.Headers.Add("Idempotency-Key", key);
		return await session.Client.SendAsync(request, ct);
	}

	private static Task<HttpResponseMessage> StartUploadAsync(ApiSession session, Guid matchId, object body, string key, CancellationToken ct) =>
		PostAsync(session, $"/api/v1/matches/{matchId}/upload-sessions", body, key, ct);

	private static Task<HttpResponseMessage> CompleteAsync(ApiSession session, Guid matchId, string id, object body, string key, CancellationToken ct) =>
		PostAsync(session, $"/api/v1/matches/{matchId}/upload-sessions/{id}/completion", body, key, ct);

	private static async Task<JsonElement> CreatedAsync(HttpResponseMessage response, CancellationToken ct)
	{
		response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(ct));
		return await response.Content.ReadFromJsonAsync<JsonElement>(ct);
	}

	private static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code, CancellationToken ct)
	{
		response.StatusCode.ShouldBe(status, await response.Content.ReadAsStringAsync(ct));
		(await response.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("code").GetString().ShouldBe(code);
	}

	private static async Task UploadPartsAsync(JsonElement started, TestRecording recording, CancellationToken ct)
	{
		using var storage = new HttpClient();
		var i = 0;
		foreach (var grant in started.GetProperty("grants").GetProperty("parts").EnumerateArray())
		{
			using var put = await RecordingTestData.PutPartAsync(storage, grant, recording.Parts[i++], ct);
			put.IsSuccessStatusCode.ShouldBeTrue(await put.Content.ReadAsStringAsync(ct));
		}
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

	private static string SessionId(JsonElement body) => body.GetProperty("session").GetProperty("id").GetString()!;

	[Fact]
	// US1 AS9, FR-025
	public async Task StartReplayWhilePendingReturnsOriginalSessionWithFreshGrants()
	{
		var ct = TestContext.Current.CancellationToken;
		var s = await StartAsync(ct);
		await using var db0 = s.Db;
		await using var fac0 = s.Factory;
		var recording = RecordingTestData.Generate(21, 5_242_880, 1000);
		var key = Guid.NewGuid().ToString();

		using var first = await StartUploadAsync(s.Coach, s.Hierarchy.MatchId, recording.Declaration(), key, ct);
		var original = await CreatedAsync(first, ct);
		var inFlight = await InFlightUploadsAsync(ct);

		using var replay = await StartUploadAsync(s.Coach, s.Hierarchy.MatchId, recording.Declaration(), key, ct);
		var replayed = await CreatedAsync(replay, ct);
		SessionId(replayed).ShouldBe(SessionId(original));
		replayed.GetProperty("session").GetProperty("state").GetString().ShouldBe("pending");
		replayed.GetProperty("grants").GetProperty("parts").GetArrayLength().ShouldBe(2);
		(await ScalarAsync(s.Db, "SELECT count(*) FROM socalytics.recording_upload_sessions", ct)).ShouldBe(1);
		(await InFlightUploadsAsync(ct)).ShouldBe(inFlight);
	}

	[Fact]
	// FR-009, FR-025: replay after completion reports the current state without grants
	public async Task StartReplayAfterCompletionReportsCompletedWithoutGrants()
	{
		var ct = TestContext.Current.CancellationToken;
		var s = await StartAsync(ct);
		await using var db0 = s.Db;
		await using var fac0 = s.Factory;
		var recording = RecordingTestData.Generate(22, 1024);
		var key = Guid.NewGuid().ToString();

		using var first = await StartUploadAsync(s.Coach, s.Hierarchy.MatchId, recording.Declaration(), key, ct);
		var original = await CreatedAsync(first, ct);
		await UploadPartsAsync(original, recording, ct);
		using var completed = await CompleteAsync(s.Coach, s.Hierarchy.MatchId, SessionId(original), ValidMapping, Guid.NewGuid().ToString(), ct);
		await CreatedAsync(completed, ct);

		using var replay = await StartUploadAsync(s.Coach, s.Hierarchy.MatchId, recording.Declaration(), key, ct);
		var replayed = await CreatedAsync(replay, ct);
		SessionId(replayed).ShouldBe(SessionId(original));
		replayed.GetProperty("session").GetProperty("state").GetString().ShouldBe("completed");
		replayed.TryGetProperty("grants", out _).ShouldBeFalse();
	}

	[Fact]
	// FR-034: the state is derived on read, the sweep does not run during the test
	public async Task StartReplayAfterExpiryReportsExpiredWithoutGrants()
	{
		var ct = TestContext.Current.CancellationToken;
		var s = await StartAsync(ct, new Dictionary<string, string?> { ["SessionLifetime"] = "00:00:02", ["ExpirySweepInterval"] = "01:00:00" });
		await using var db0 = s.Db;
		await using var fac0 = s.Factory;
		var recording = RecordingTestData.Generate(23, 1024);
		var key = Guid.NewGuid().ToString();

		using var first = await StartUploadAsync(s.Coach, s.Hierarchy.MatchId, recording.Declaration(), key, ct);
		var original = await CreatedAsync(first, ct);
		await Task.Delay(TimeSpan.FromSeconds(3), ct);

		using var replay = await StartUploadAsync(s.Coach, s.Hierarchy.MatchId, recording.Declaration(), key, ct);
		var replayed = await CreatedAsync(replay, ct);
		SessionId(replayed).ShouldBe(SessionId(original));
		replayed.GetProperty("session").GetProperty("state").GetString().ShouldBe("expired");
		replayed.TryGetProperty("grants", out _).ShouldBeFalse();
	}

	[Fact]
	// Edges: retry-key reuse with different content; same key on another Match or operation
	public async Task ReusedKeyWithDifferentBodyConflictsAndOtherScopesSucceedIndependently()
	{
		var ct = TestContext.Current.CancellationToken;
		var s = await StartAsync(ct);
		await using var db0 = s.Db;
		await using var fac0 = s.Factory;
		var recording = RecordingTestData.Generate(24, 1024);
		var other = RecordingTestData.Generate(25, 2048);
		var key = Guid.NewGuid().ToString();

		using var first = await StartUploadAsync(s.Coach, s.Hierarchy.MatchId, recording.Declaration(), key, ct);
		var original = await CreatedAsync(first, ct);
		await UploadPartsAsync(original, recording, ct);

		using var different = await StartUploadAsync(s.Coach, s.Hierarchy.MatchId, other.Declaration(), key, ct);
		await AssertProblemAsync(different, HttpStatusCode.Conflict, "idempotency-key-reused", ct);
		(await ScalarAsync(s.Db, "SELECT count(*) FROM socalytics.recording_upload_sessions", ct)).ShouldBe(1);

		var (_, secondMatchId) = await new ClubHierarchyBuilder(s.Factory, s.Admin).CreateTeamWithMatchAsync(s.Hierarchy.SeasonId, ct);
		using var otherMatch = await StartUploadAsync(s.Admin, secondMatchId, recording.Declaration(), key, ct);
		SessionId(await CreatedAsync(otherMatch, ct)).ShouldNotBe(SessionId(original));

		// The start key reused for the completion operation is a different scope.
		using var completion = await CompleteAsync(s.Coach, s.Hierarchy.MatchId, SessionId(original), ValidMapping, key, ct);
		await CreatedAsync(completion, ct);
		(await ScalarAsync(s.Db, "SELECT count(*) FROM socalytics.recording_upload_sessions", ct)).ShouldBe(2);
	}

	[Fact]
	// Edge: Duplicate completion
	public async Task CompletionReplayReturnsOriginalIdentitiesAndNewKeyConflicts()
	{
		var ct = TestContext.Current.CancellationToken;
		var s = await StartAsync(ct);
		await using var db0 = s.Db;
		await using var fac0 = s.Factory;
		var recording = RecordingTestData.Generate(26, 1024);
		using var started = await StartUploadAsync(s.Coach, s.Hierarchy.MatchId, recording.Declaration(), Guid.NewGuid().ToString(), ct);
		var body = await CreatedAsync(started, ct);
		await UploadPartsAsync(body, recording, ct);
		var id = SessionId(body);
		var key = Guid.NewGuid().ToString();

		using var first = await CompleteAsync(s.Coach, s.Hierarchy.MatchId, id, ValidMapping, key, ct);
		var original = await CreatedAsync(first, ct);
		using var replay = await CompleteAsync(s.Coach, s.Hierarchy.MatchId, id, ValidMapping, key, ct);
		var replayed = await CreatedAsync(replay, ct);
		replayed.GetProperty("recordingVersion").GetProperty("id").GetString().ShouldBe(original.GetProperty("recordingVersion").GetProperty("id").GetString());
		replayed.GetProperty("timelineMapping").GetProperty("id").GetString().ShouldBe(original.GetProperty("timelineMapping").GetProperty("id").GetString());

		using var other = await CompleteAsync(s.Coach, s.Hierarchy.MatchId, id, ValidMapping, Guid.NewGuid().ToString(), ct);
		await AssertProblemAsync(other, HttpStatusCode.Conflict, "upload-session-completed", ct);
		(await ScalarAsync(s.Db, "SELECT count(*) FROM socalytics.recording_versions", ct)).ShouldBe(1);
	}

	[Fact]
	// FR-026: failed requests record no outcome
	public async Task FailedCompletionRecordsNoOutcomeAndCanBeRetriedAfterCorrection()
	{
		var ct = TestContext.Current.CancellationToken;
		var s = await StartAsync(ct);
		await using var db0 = s.Db;
		await using var fac0 = s.Factory;
		var recording = RecordingTestData.Generate(27, 1024);
		using var started = await StartUploadAsync(s.Coach, s.Hierarchy.MatchId, recording.Declaration(), Guid.NewGuid().ToString(), ct);
		var body = await CreatedAsync(started, ct);
		await UploadPartsAsync(body, recording, ct);
		var id = SessionId(body);
		var key = Guid.NewGuid().ToString();
		var outcomes = await ScalarAsync(s.Db, "SELECT count(*) FROM socalytics.recording_retry_outcomes", ct);

		var invalid = new { timelineMapping = new { spans = new[] { new { mediaStartSeconds = 5, mediaEndSeconds = 5, matchStartSeconds = 0 } } } };
		using var failed = await CompleteAsync(s.Coach, s.Hierarchy.MatchId, id, invalid, key, ct);
		await AssertProblemAsync(failed, HttpStatusCode.BadRequest, "timeline-mapping-invalid", ct);
		(await ScalarAsync(s.Db, "SELECT count(*) FROM socalytics.recording_retry_outcomes", ct)).ShouldBe(outcomes);

		using var corrected = await CompleteAsync(s.Coach, s.Hierarchy.MatchId, id, ValidMapping, key, ct);
		await CreatedAsync(corrected, ct);
	}

	[Fact]
	// Edge: Replay after revocation
	public async Task RevokedCoachReplayIsForbiddenWithoutDisclosingOutcome()
	{
		var ct = TestContext.Current.CancellationToken;
		var s = await StartAsync(ct);
		await using var db0 = s.Db;
		await using var fac0 = s.Factory;
		var recording = RecordingTestData.Generate(28, 1024);
		var startKey = Guid.NewGuid().ToString();
		var completeKey = Guid.NewGuid().ToString();
		using var started = await StartUploadAsync(s.Coach, s.Hierarchy.MatchId, recording.Declaration(), startKey, ct);
		var body = await CreatedAsync(started, ct);
		await UploadPartsAsync(body, recording, ct);
		var id = SessionId(body);
		using var completed = await CompleteAsync(s.Coach, s.Hierarchy.MatchId, id, ValidMapping, completeKey, ct);
		await CreatedAsync(completed, ct);

		await using (var c = new NpgsqlConnection(s.Db.AppConnectionString))
		{
			await c.OpenAsync(ct);
			await using var cmd = new NpgsqlCommand("DELETE FROM socalytics.team_role_assignment WHERE member_account_id = @m", c);
			cmd.Parameters.AddWithValue("m", s.CoachId);
			await cmd.ExecuteNonQueryAsync(ct);
		}

		using var startReplay = await StartUploadAsync(s.Coach, s.Hierarchy.MatchId, recording.Declaration(), startKey, ct);
		startReplay.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
		(await startReplay.Content.ReadAsStringAsync(ct)).ShouldNotContain(id);
		using var completeReplay = await CompleteAsync(s.Coach, s.Hierarchy.MatchId, id, ValidMapping, completeKey, ct);
		completeReplay.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
		(await completeReplay.Content.ReadAsStringAsync(ct)).ShouldNotContain("recordingVersion");
	}

	[Fact]
	// FR-025: concurrent identical starts yield one session
	public async Task ConcurrentIdenticalStartsYieldOneSession()
	{
		var ct = TestContext.Current.CancellationToken;
		var s = await StartAsync(ct);
		await using var db0 = s.Db;
		await using var fac0 = s.Factory;
		var recording = RecordingTestData.Generate(29, 1024);
		var key = Guid.NewGuid().ToString();

		var responses = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ =>
			StartUploadAsync(s.Coach, s.Hierarchy.MatchId, recording.Declaration(), key, ct)));
		var ids = new HashSet<string>();
		foreach (var response in responses)
		{
			using (response)
			{
				ids.Add(SessionId(await CreatedAsync(response, ct)));
			}
		}

		ids.Count.ShouldBe(1);
		(await ScalarAsync(s.Db, "SELECT count(*) FROM socalytics.recording_upload_sessions", ct)).ShouldBe(1);
	}
}
