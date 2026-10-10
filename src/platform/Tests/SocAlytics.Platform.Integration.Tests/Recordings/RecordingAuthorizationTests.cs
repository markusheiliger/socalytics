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

// US1 AS4-AS6, FR-002-FR-006, SC-003
public sealed class RecordingAuthorizationTests(PostgresContainerFixture postgres, RustFsContainerFixture rustFs)
{
	private static readonly string[] RecordingTables =
	[
		"recording_upload_sessions", "recording_versions", "recording_timeline_mappings", "recording_set_versions",
		"recording_set_members", "recording_retry_outcomes", "recording_finalized_events",
	];

	private static readonly object Mapping = new { timelineMapping = new { spans = new[] { new { mediaStartSeconds = 0, mediaEndSeconds = 10, matchStartSeconds = 5 } } } };

	private sealed record Setup(
		IsolatedDatabase Db, PlatformApiFactory Factory, ClubHierarchyBuilder Builder, ApiSession Admin, ApiSession Coach, Guid CoachId,
		ApiSession Viewer, ClubHierarchy Hierarchy, Guid OtherMatchId);

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
		var viewerId = await TestMembers.SeedAsync(db, "viewer-1", cancellationToken: ct);
		var admin = await ApiSession.SignInAsync(factory, "admin-1", TestMembers.DefaultPassword, ct);
		var builder = new ClubHierarchyBuilder(factory, admin);
		var hierarchy = await builder.CreateAsync(ct);
		var (_, otherMatchId) = await builder.CreateTeamWithMatchAsync(hierarchy.SeasonId, ct);
		await AssignAsync(db, coachId, hierarchy.TeamId, "coach", ct);
		await AssignAsync(db, viewerId, hierarchy.TeamId, "viewer", ct);
		var coach = await ApiSession.SignInAsync(factory, "coach-1", TestMembers.DefaultPassword, ct);
		var viewer = await ApiSession.SignInAsync(factory, "viewer-1", TestMembers.DefaultPassword, ct);
		return new Setup(db, factory, builder, admin, coach, coachId, viewer, hierarchy, otherMatchId);
	}

	private static async Task AssignAsync(IsolatedDatabase db, Guid memberId, Guid teamId, string role, CancellationToken ct)
	{
		await using var c = new NpgsqlConnection(db.AppConnectionString);
		await c.OpenAsync(ct);
		await using var cmd = new NpgsqlCommand(
			"INSERT INTO socalytics.team_role_assignment (member_account_id, team_id, role, assigned_at, assigned_by_account_id) VALUES (@m, @t, @r, now(), @m)", c);
		cmd.Parameters.AddWithValue("m", memberId);
		cmd.Parameters.AddWithValue("t", teamId);
		cmd.Parameters.AddWithValue("r", role);
		await cmd.ExecuteNonQueryAsync(ct);
	}

	private static async Task<long> ScalarAsync(IsolatedDatabase db, string sql, CancellationToken ct)
	{
		await using var connection = new NpgsqlConnection(db.MigratorConnectionString);
		await connection.OpenAsync(ct);
		await using var command = new NpgsqlCommand(sql, connection);
		return Convert.ToInt64(await command.ExecuteScalarAsync(ct));
	}

	// Row counts plus a checksum of every row so changed rows are detected too.
	private static async Task<string> SnapshotAsync(IsolatedDatabase db, CancellationToken ct)
	{
		var parts = new List<string>();
		foreach (var table in RecordingTables)
		{
			await using var connection = new NpgsqlConnection(db.MigratorConnectionString);
			await connection.OpenAsync(ct);
			await using var command = new NpgsqlCommand($"SELECT count(*) || ':' || coalesce(md5(string_agg(t::text, '|' ORDER BY t::text)), '') FROM socalytics.{table} t", connection);
			parts.Add($"{table}={await command.ExecuteScalarAsync(ct)}");
		}

		return string.Join(';', parts);
	}

	private async Task<int> InFlightUploadsAsync(CancellationToken ct)
	{
		using var s3 = rustFs.CreateS3Client();
		var listed = await s3.ListMultipartUploadsAsync(new ListMultipartUploadsRequest { BucketName = rustFs.Bucket }, ct);
		return listed.MultipartUploads?.Count ?? 0;
	}

	private static Task<long> DenialsAsync(IsolatedDatabase db, CancellationToken ct) =>
		ScalarAsync(db, "SELECT count(*) FROM socalytics.security_audit_event WHERE event_type = 'authorization.denied'", ct);

	private static async Task<HttpResponseMessage> SendAsync(HttpClient client, string? csrf, HttpMethod method, string path, object? body, CancellationToken ct)
	{
		using var request = new HttpRequestMessage(method, path);
		if (body is not null)
		{
			request.Content = JsonContent.Create(body);
		}

		if (csrf is not null)
		{
			request.Headers.Add("X-CSRF-Token", csrf);
		}

		if (method == HttpMethod.Post)
		{
			request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
		}

		return await client.SendAsync(request, ct);
	}

	private static async Task<(string SessionId, HttpResponseMessage Response)> StartUploadAsync(ApiSession session, Guid matchId, TestRecording recording, CancellationToken ct)
	{
		var response = await SendAsync(session.Client, session.AntiforgeryToken, HttpMethod.Post, $"/api/v1/matches/{matchId}/upload-sessions", recording.Declaration(), ct);
		if (response.StatusCode != HttpStatusCode.Created)
		{
			return (string.Empty, response);
		}

		var body = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
		return (body.GetProperty("session").GetProperty("id").GetString()!, response);
	}

	private static IEnumerable<(string Name, HttpMethod Method, string Path, object? Body)> Operations(Guid matchId, string sessionId, TestRecording recording)
	{
		var baseUrl = $"/api/v1/matches/{matchId}/upload-sessions";
		yield return ("start", HttpMethod.Post, baseUrl, recording.Declaration());
		yield return ("grants", HttpMethod.Post, $"{baseUrl}/{sessionId}/grants", new { partNumbers = new[] { 1 } });
		yield return ("completion", HttpMethod.Post, $"{baseUrl}/{sessionId}/completion", Mapping);
		yield return ("read", HttpMethod.Get, $"{baseUrl}/{sessionId}", null);
	}

	private async Task AssertDeniedAsync(
		Setup s, string caller, HttpClient client, string? csrf, Guid matchId, string sessionId, TestRecording recording,
		HttpStatusCode expected, bool audited, CancellationToken ct)
	{
		var inFlight = await InFlightUploadsAsync(ct);
		foreach (var (name, method, path, body) in Operations(matchId, sessionId, recording))
		{
			var rows = await SnapshotAsync(s.Db, ct);
			var denials = await DenialsAsync(s.Db, ct);
			using var response = await SendAsync(client, csrf, method, path, body, ct);
			response.StatusCode.ShouldBe(expected, $"{caller} {name}: {await response.Content.ReadAsStringAsync(ct)}");
			(await SnapshotAsync(s.Db, ct)).ShouldBe(rows, $"{caller} {name} changed Recordings rows");
			(await InFlightUploadsAsync(ct)).ShouldBe(inFlight, $"{caller} {name} created a multipart upload");
			(await DenialsAsync(s.Db, ct)).ShouldBe(denials + (audited ? 1 : 0), $"{caller} {name} audit");
		}
	}

	[Fact]
	// US1 AS4, AS5, FR-002-FR-004, SC-003
	public async Task OtherTeamCoachViewerAndAnonymousCallersAreDenied()
	{
		var ct = TestContext.Current.CancellationToken;
		var s = await StartAsync(ct);
		await using var db0 = s.Db;
		await using var fac0 = s.Factory;
		var recording = RecordingTestData.Generate(7, 1024);

		// Session on a Team the Coach does not belong to.
		var admin = s.Admin;
		var (otherSessionId, started) = await StartUploadAsync(admin, s.OtherMatchId, recording, ct);
		started.Dispose();
		otherSessionId.ShouldNotBeEmpty();
		var (ownSessionId, ownStarted) = await StartUploadAsync(admin, s.Hierarchy.MatchId, recording, ct);
		ownStarted.Dispose();

		await AssertDeniedAsync(s, "other-team coach", s.Coach.Client, s.Coach.AntiforgeryToken, s.OtherMatchId, otherSessionId, recording, HttpStatusCode.Forbidden, true, ct);
		await AssertDeniedAsync(s, "viewer", s.Viewer.Client, s.Viewer.AntiforgeryToken, s.Hierarchy.MatchId, ownSessionId, recording, HttpStatusCode.Forbidden, true, ct);

		using var anonymous = s.Factory.CreateApiClient();
		await AssertDeniedAsync(s, "anonymous", anonymous, null, s.Hierarchy.MatchId, ownSessionId, recording, HttpStatusCode.Unauthorized, false, ct);
	}

	[Fact]
	// Edge: Match deleted or not found
	public async Task MissingMatchIsNotFoundAndAudited()
	{
		var ct = TestContext.Current.CancellationToken;
		var s = await StartAsync(ct);
		await using var db0 = s.Db;
		await using var fac0 = s.Factory;
		var recording = RecordingTestData.Generate(8, 1024);

		await AssertDeniedAsync(s, "coach", s.Coach.Client, s.Coach.AntiforgeryToken, Guid.NewGuid(), Guid.NewGuid().ToString(), recording, HttpStatusCode.NotFound, true, ct);
	}

	[Fact]
	// US1 AS6, Edge: Match in an archived Season
	public async Task ArchivedSeasonRefusesMutations()
	{
		var ct = TestContext.Current.CancellationToken;
		var s = await StartAsync(ct);
		await using var db0 = s.Db;
		await using var fac0 = s.Factory;
		var recording = RecordingTestData.Generate(9, 1024);
		var (sessionId, started) = await StartUploadAsync(s.Coach, s.Hierarchy.MatchId, recording, ct);
		started.Dispose();
		sessionId.ShouldNotBeEmpty();
		await s.Builder.ArchiveSeasonAsync(s.Hierarchy.SeasonId, ct);

		var inFlight = await InFlightUploadsAsync(ct);
		var rows = await SnapshotAsync(s.Db, ct);
		foreach (var (name, method, path, body) in Operations(s.Hierarchy.MatchId, sessionId, recording).Where(o => o.Method == HttpMethod.Post))
		{
			using var response = await SendAsync(s.Coach.Client, s.Coach.AntiforgeryToken, method, path, body, ct);
			response.StatusCode.ShouldBe(HttpStatusCode.Conflict, $"{name}: {await response.Content.ReadAsStringAsync(ct)}");
			(await response.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("code").GetString().ShouldBe("season-archived");
		}

		(await SnapshotAsync(s.Db, ct)).ShouldBe(rows);
		(await InFlightUploadsAsync(ct)).ShouldBe(inFlight);
	}

	[Fact]
	// Edge: Membership or role revoked mid-upload
	public async Task RevokedCoachIsForbiddenAtGrantsAndCompletion()
	{
		var ct = TestContext.Current.CancellationToken;
		var s = await StartAsync(ct);
		await using var db0 = s.Db;
		await using var fac0 = s.Factory;
		var recording = RecordingTestData.Generate(10, 1024);
		var (sessionId, started) = await StartUploadAsync(s.Coach, s.Hierarchy.MatchId, recording, ct);
		started.Dispose();
		sessionId.ShouldNotBeEmpty();

		await using (var c = new NpgsqlConnection(s.Db.AppConnectionString))
		{
			await c.OpenAsync(ct);
			await using var cmd = new NpgsqlCommand("DELETE FROM socalytics.team_role_assignment WHERE member_account_id = @m", c);
			cmd.Parameters.AddWithValue("m", s.CoachId);
			await cmd.ExecuteNonQueryAsync(ct);
		}

		var inFlight = await InFlightUploadsAsync(ct);
		foreach (var (name, method, path, body) in Operations(s.Hierarchy.MatchId, sessionId, recording).Where(o => o.Name is "grants" or "completion"))
		{
			var rows = await SnapshotAsync(s.Db, ct);
			var denials = await DenialsAsync(s.Db, ct);
			using var response = await SendAsync(s.Coach.Client, s.Coach.AntiforgeryToken, method, path, body, ct);
			response.StatusCode.ShouldBe(HttpStatusCode.Forbidden, $"{name}: {await response.Content.ReadAsStringAsync(ct)}");
			(await SnapshotAsync(s.Db, ct)).ShouldBe(rows);
			(await DenialsAsync(s.Db, ct)).ShouldBe(denials + 1);
		}

		(await InFlightUploadsAsync(ct)).ShouldBe(inFlight);
	}
}
