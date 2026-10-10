using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;
using Shouldly;
using SocAlytics.Platform.Integration.Tests.IdentityAccess.Support;
using SocAlytics.Platform.Integration.Tests.Infrastructure;
using SocAlytics.Platform.Integration.Tests.Recordings.Support;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.Recordings;

public sealed class TimelineMappingRevisionTests(PostgresContainerFixture postgres, RustFsContainerFixture rustFs)
{
	private static Task<HttpResponseMessage> PostAsync(ApiSession session, string path, object body, string key, CancellationToken ct)
	{
		var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
		request.Headers.Add("X-CSRF-Token", session.AntiforgeryToken);
		request.Headers.Add("Idempotency-Key", key);
		return session.Client.SendAsync(request, ct);
	}

	private static object Spans(int matchStart) =>
		new { spans = new[] { new { mediaStartSeconds = 0, mediaEndSeconds = 10, matchStartSeconds = matchStart } } };

	private static async Task<(Guid Version, Guid Mapping)> UploadAsync(ApiSession session, Guid matchId, int seed, CancellationToken ct)
	{
		var recording = RecordingTestData.Generate(seed, 2048);
		using var started = await PostAsync(session, $"/api/v1/matches/{matchId}/upload-sessions", recording.Declaration(), Guid.NewGuid().ToString(), ct);
		started.StatusCode.ShouldBe(HttpStatusCode.Created, await started.Content.ReadAsStringAsync(ct));
		var body = await started.Content.ReadFromJsonAsync<JsonElement>(ct);
		using var storage = new HttpClient();
		using (var put = await RecordingTestData.PutPartAsync(storage, body.GetProperty("grants").GetProperty("parts")[0], recording.Parts[0], ct))
		{
			put.IsSuccessStatusCode.ShouldBeTrue();
		}

		var id = body.GetProperty("session").GetProperty("id").GetString();
		using var completed = await PostAsync(
			session, $"/api/v1/matches/{matchId}/upload-sessions/{id}/completion", new { timelineMapping = Spans(0) }, Guid.NewGuid().ToString(), ct);
		completed.StatusCode.ShouldBe(HttpStatusCode.Created, await completed.Content.ReadAsStringAsync(ct));
		var result = await completed.Content.ReadFromJsonAsync<JsonElement>(ct);
		return (result.GetProperty("recordingVersion").GetProperty("id").GetGuid(), result.GetProperty("timelineMapping").GetProperty("id").GetGuid());
	}

	private static Task<HttpResponseMessage> ReviseAsync(ApiSession session, Guid matchId, Guid versionId, object body, string key, CancellationToken ct) =>
		PostAsync(session, $"/api/v1/matches/{matchId}/recording-versions/{versionId}/timeline-mappings", body, key, ct);

	private static async Task ExecuteAsync(FinalizationSetup s, string sql, CancellationToken ct)
	{
		await using var connection = new NpgsqlConnection(s.Db.MigratorConnectionString);
		await connection.OpenAsync(ct);
		await using var command = new NpgsqlCommand(sql, connection);
		await command.ExecuteNonQueryAsync(ct);
	}

	[Fact]
	// US3 AS1, FR-013, FR-025, FR-030
	public async Task RevisionCreatesNewMappingAndIdenticalContentReturnsExisting()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var s = await FinalizationSetup.StartAsync(postgres, rustFs, ct);
		var match = s.Hierarchy.MatchId;
		var (version, original) = await UploadAsync(s.Coach, match, 1, ct);
		var originalDigest = await ScalarTextAsync(s, $"SELECT mapping_digest FROM socalytics.recording_timeline_mappings WHERE id = '{original}'", ct);

		var key = Guid.NewGuid().ToString();
		using var revised = await ReviseAsync(s.Coach, match, version, Spans(7), key, ct);
		revised.StatusCode.ShouldBe(HttpStatusCode.Created, await revised.Content.ReadAsStringAsync(ct));
		var json = await revised.Content.ReadFromJsonAsync<JsonElement>(ct);
		var newId = json.GetProperty("id").GetGuid();
		newId.ShouldNotBe(original);
		json.GetProperty("recordingVersionId").GetGuid().ShouldBe(version);
		json.GetProperty("digest").GetString().ShouldNotBe(originalDigest);
		revised.Headers.Location!.ToString().ShouldBe($"/api/v1/matches/{match}/timeline-mappings/{newId}");
		(await ScalarTextAsync(s, $"SELECT mapping_digest FROM socalytics.recording_timeline_mappings WHERE id = '{original}'", ct)).ShouldBe(originalDigest);
		(await s.ScalarAsync("SELECT count(*) FROM socalytics.recording_timeline_mappings", ct)).ShouldBe(2);

		using var replay = await ReviseAsync(s.Coach, match, version, Spans(7), key, ct);
		replay.StatusCode.ShouldBe(HttpStatusCode.Created);
		(await replay.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("id").GetGuid().ShouldBe(newId);
		using var reused = await ReviseAsync(s.Coach, match, version, Spans(8), key, ct);
		reused.StatusCode.ShouldBe(HttpStatusCode.Conflict);
		(await reused.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("code").GetString().ShouldBe("idempotency-key-reused");

		var sameKey = Guid.NewGuid().ToString();
		using var same = await ReviseAsync(s.Coach, match, version, Spans(7), sameKey, ct);
		same.StatusCode.ShouldBe(HttpStatusCode.OK, await same.Content.ReadAsStringAsync(ct));
		(await same.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("id").GetGuid().ShouldBe(newId);
		using var sameReplay = await ReviseAsync(s.Coach, match, version, Spans(7), sameKey, ct);
		sameReplay.StatusCode.ShouldBe(HttpStatusCode.OK);
		(await sameReplay.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("id").GetGuid().ShouldBe(newId);
		(await s.ScalarAsync("SELECT count(*) FROM socalytics.recording_timeline_mappings", ct)).ShouldBe(2);

		(await s.ScalarAsync($"SELECT count(*) FROM socalytics.security_audit_event WHERE event_type = 'recording.timeline-mapping.revise' AND resource_id = '{newId}' AND outcome = 'succeeded' AND resource_type = 'timeline-mapping'", ct)).ShouldBe(1);
		(await s.ScalarAsync($"SELECT count(*) FROM socalytics.security_audit_event WHERE event_type = 'recording.timeline-mapping.revise' AND resource_id = '{newId}' AND outcome = 'unchanged'", ct)).ShouldBe(1);
	}

	[Fact]
	// US3 AS1: invalid mapping
	public async Task InvalidMappingIsRejected()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var s = await FinalizationSetup.StartAsync(postgres, rustFs, ct);
		var (version, _) = await UploadAsync(s.Coach, s.Hierarchy.MatchId, 2, ct);
		using var response = await ReviseAsync(s.Coach, s.Hierarchy.MatchId, version, new { spans = Array.Empty<object>() }, Guid.NewGuid().ToString(), ct);
		response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, await response.Content.ReadAsStringAsync(ct));
		(await response.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("code").GetString().ShouldBe("timeline-mapping-invalid");
		(await s.ScalarAsync("SELECT count(*) FROM socalytics.recording_timeline_mappings", ct)).ShouldBe(1);
	}

	[Fact]
	// US3 AS1, FR-004: denied callers create no row
	public async Task DeniedCallersCreateNoRow()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var s = await FinalizationSetup.StartAsync(postgres, rustFs, ct);
		var match = s.Hierarchy.MatchId;
		var (version, _) = await UploadAsync(s.Coach, match, 3, ct);
		var builder = new ClubHierarchyBuilder(s.Factory, s.Admin);
		var (_, otherMatch) = await builder.CreateTeamWithMatchAsync(s.Hierarchy.SeasonId, ct);
		var other = await UploadAsync(s.Admin, otherMatch, 4, ct);
		var viewerId = await TestMembers.SeedAsync(s.Db, "viewer-1", cancellationToken: ct);
		await ExecuteAsync(s, $"INSERT INTO socalytics.team_role_assignment (member_account_id, team_id, role, assigned_at, assigned_by_account_id) VALUES ('{viewerId}', '{s.Hierarchy.TeamId}', 'viewer', now(), '{viewerId}')", ct);
		var viewer = await ApiSession.SignInAsync(s.Factory, "viewer-1", TestMembers.DefaultPassword, ct);

		async Task AssertAsync(string name, ApiSession session, Guid matchId, Guid versionId, HttpStatusCode expected)
		{
			using var response = await ReviseAsync(session, matchId, versionId, Spans(9), Guid.NewGuid().ToString(), ct);
			response.StatusCode.ShouldBe(expected, $"{name}: {await response.Content.ReadAsStringAsync(ct)}");
			(await s.ScalarAsync("SELECT count(*) FROM socalytics.recording_timeline_mappings", ct)).ShouldBe(2, name);
		}

		await AssertAsync("viewer", viewer, match, version, HttpStatusCode.Forbidden);
		await AssertAsync("other-team coach", s.Coach, otherMatch, other.Version, HttpStatusCode.Forbidden);
		await AssertAsync("foreign version", s.Coach, match, other.Version, HttpStatusCode.NotFound);

		using (var anonymous = s.Factory.CreateApiClient())
		{
			using var response = await anonymous.PostAsJsonAsync($"/api/v1/matches/{match}/recording-versions/{version}/timeline-mappings", Spans(9), ct);
			response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
		}

		await builder.ArchiveSeasonAsync(s.Hierarchy.SeasonId, ct);
		using (var archived = await ReviseAsync(s.Coach, match, version, Spans(9), Guid.NewGuid().ToString(), ct))
		{
			archived.StatusCode.ShouldBe(HttpStatusCode.Conflict);
			(await archived.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("code").GetString().ShouldBe("season-archived");
		}

		await ExecuteAsync(s, $"DELETE FROM socalytics.team_role_assignment WHERE member_account_id = '{s.CoachId}'", ct);
		await AssertAsync("revoked coach", s.Coach, match, version, HttpStatusCode.Forbidden);
	}

	private static async Task<string> ScalarTextAsync(FinalizationSetup s, string sql, CancellationToken ct)
	{
		await using var connection = new NpgsqlConnection(s.Db.MigratorConnectionString);
		await connection.OpenAsync(ct);
		await using var command = new NpgsqlCommand(sql, connection);
		return (string)(await command.ExecuteScalarAsync(ct))!;
	}
}
