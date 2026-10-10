using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using SocAlytics.Platform.Integration.Tests.IdentityAccess.Support;
using SocAlytics.Platform.Integration.Tests.Infrastructure;
using SocAlytics.Platform.Integration.Tests.Recordings.Support;
using SocAlytics.Platform.Migrator;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.Recordings;

public sealed class ImmutabilityTests(PostgresContainerFixture postgres, RustFsContainerFixture rustFs)
{
	private static readonly Dictionary<string, string?> Config = new()
	{
		["ClubDisplayName"] = "Recordings Club",
		["FirstClubAdmin:AccountName"] = "rec-bootstrap",
		["FirstClubAdmin:InitialPassword"] = "Initial-Admin-Pass-1234",
	};

	private static readonly string[] ImmutableTables =
	[
		"recording_versions",
		"recording_timeline_mappings",
		"recording_set_versions",
		"recording_set_members",
		"recording_retry_outcomes",
		"recording_finalized_events",
	];

	private static async Task ExecAsync(string connectionString, string sql, Action<NpgsqlParameterCollection>? parameters, CancellationToken ct)
	{
		await using var c = new NpgsqlConnection(connectionString);
		await c.OpenAsync(ct);
		await using var cmd = new NpgsqlCommand(sql, c);
		parameters?.Invoke(cmd.Parameters);
		await cmd.ExecuteNonQueryAsync(ct);
	}


	private static Task<HttpResponseMessage> PostAsync(ApiSession session, string path, object body, CancellationToken ct)
	{
		var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
		request.Headers.Add("X-CSRF-Token", session.AntiforgeryToken);
		request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
		return session.Client.SendAsync(request, ct);
	}

	private static async Task<(Guid Version, Guid Mapping)> UploadAsync(ApiSession session, Guid matchId, int seed, CancellationToken ct)
	{
		var recording = RecordingTestData.Generate(seed, 2048);
		using var started = await PostAsync(session, $"/api/v1/matches/{matchId}/upload-sessions", recording.Declaration(), ct);
		started.StatusCode.ShouldBe(HttpStatusCode.Created, await started.Content.ReadAsStringAsync(ct));
		var body = await started.Content.ReadFromJsonAsync<JsonElement>(ct);
		using var storage = new HttpClient();
		using (var put = await RecordingTestData.PutPartAsync(storage, body.GetProperty("grants").GetProperty("parts")[0], recording.Parts[0], ct))
		{
			put.IsSuccessStatusCode.ShouldBeTrue();
		}

		var id = body.GetProperty("session").GetProperty("id").GetString();
		var spans = new[] { new { mediaStartSeconds = 0, mediaEndSeconds = 10, matchStartSeconds = 0 } };
		using var completed = await PostAsync(session, $"/api/v1/matches/{matchId}/upload-sessions/{id}/completion", new { timelineMapping = new { spans } }, ct);
		completed.StatusCode.ShouldBe(HttpStatusCode.Created, await completed.Content.ReadAsStringAsync(ct));
		var result = await completed.Content.ReadFromJsonAsync<JsonElement>(ct);
		return (result.GetProperty("recordingVersion").GetProperty("id").GetGuid(), result.GetProperty("timelineMapping").GetProperty("id").GetGuid());
	}

	private static async Task<Guid> FinalizeAsync(ApiSession session, Guid matchId, IEnumerable<(Guid Version, Guid Mapping)> members, CancellationToken ct)
	{
		var body = new { members = members.Select(m => new { recordingVersionId = m.Version, timelineMappingId = m.Mapping }).ToArray() };
		using var response = await PostAsync(session, $"/api/v1/matches/{matchId}/recording-set-versions", body, ct);
		response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(ct));
		return (await response.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("id").GetGuid();
	}

	private static async Task<string> SnapshotAsync(FinalizationSetup s, string sql, CancellationToken ct)
	{
		await using var connection = new NpgsqlConnection(s.Db.MigratorConnectionString);
		await connection.OpenAsync(ct);
		await using var command = new NpgsqlCommand($"SELECT coalesce(string_agg(r::text, E'\\n' ORDER BY r::text), '') FROM ({sql}) r", connection);
		return (string)(await command.ExecuteScalarAsync(ct))!;
	}

	[Fact]
	// US3 AS2-AS3, FR-015, FR-018, SC-005
	public async Task CorrectionsOnlyAddVersionsAndLeaveEarlierOnesUnchanged()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var s = await FinalizationSetup.StartAsync(postgres, rustFs, ct);
		var match = s.Hierarchy.MatchId;
		var a = await UploadAsync(s.Coach, match, 71, ct);
		var versionBefore = await SnapshotAsync(s, $"SELECT * FROM socalytics.recording_versions WHERE id = '{a.Version}'", ct);
		var mappingBefore = await SnapshotAsync(s, $"SELECT * FROM socalytics.recording_timeline_mappings WHERE id = '{a.Mapping}'", ct);

		var corrected = await UploadAsync(s.Coach, match, 72, ct);
		corrected.Version.ShouldNotBe(a.Version);
		corrected.Mapping.ShouldNotBe(a.Mapping);
		(await s.ScalarAsync("SELECT count(*) FROM socalytics.recording_versions", ct)).ShouldBe(2);
		(await SnapshotAsync(s, $"SELECT * FROM socalytics.recording_versions WHERE id = '{a.Version}'", ct)).ShouldBe(versionBefore);
		(await SnapshotAsync(s, $"SELECT * FROM socalytics.recording_timeline_mappings WHERE id = '{a.Mapping}'", ct)).ShouldBe(mappingBefore);

		var set1 = await FinalizeAsync(s.Coach, match, [a, corrected], ct);
		var setSql = (Guid id) =>
			$"SELECT * FROM socalytics.recording_set_versions WHERE id = '{id}'";
		var memberSql = (Guid id) =>
			$"SELECT * FROM socalytics.recording_set_members WHERE recording_set_version_id = '{id}'";
		var eventSql = (Guid id) =>
			$"SELECT * FROM socalytics.recording_finalized_events WHERE recording_set_version_id = '{id}'";
		var setBefore = await SnapshotAsync(s, setSql(set1), ct);
		var membersBefore = await SnapshotAsync(s, memberSql(set1), ct);
		var eventBefore = await SnapshotAsync(s, eventSql(set1), ct);

		var set2 = await FinalizeAsync(s.Coach, match, [corrected, a], ct);
		set2.ShouldNotBe(set1);
		(await s.ScalarAsync("SELECT count(*) FROM socalytics.recording_set_versions", ct)).ShouldBe(2);
		(await s.ScalarAsync("SELECT count(*) FROM socalytics.recording_finalized_events", ct)).ShouldBe(2);
		(await s.ScalarAsync($"SELECT count(*) FROM socalytics.recording_finalized_events WHERE recording_set_version_id = '{set2}'", ct)).ShouldBe(1);
		(await SnapshotAsync(s, setSql(set1), ct)).ShouldBe(setBefore);
		(await SnapshotAsync(s, memberSql(set1), ct)).ShouldBe(membersBefore);
		(await SnapshotAsync(s, eventSql(set1), ct)).ShouldBe(eventBefore);
	}

	[Fact]
	// US3 AS4, FR-021: no modifying route exists, including for a Club Admin
	public async Task NoPutPatchOrDeleteRouteExistsForRecordingResources()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var s = await FinalizationSetup.StartAsync(postgres, rustFs, ct);
		var endpoints = s.Factory.Services.GetServices<EndpointDataSource>().SelectMany(d => d.Endpoints).OfType<RouteEndpoint>()
			.Where(e => (e.RoutePattern.RawText ?? string.Empty).Contains("/matches/{matchId", StringComparison.Ordinal))
			.ToList();
		endpoints.ShouldContain(e => e.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.Contains("POST"));
		string[] resources = ["recording-versions", "timeline-mappings", "recording-set-versions", "upload-sessions"];
		string[] forbidden = ["PUT", "PATCH", "DELETE"];
		var offending = endpoints
			.Where(e => resources.Any(r => e.RoutePattern.RawText!.Contains(r, StringComparison.Ordinal)))
			.Where(e => e.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.Any(m => forbidden.Contains(m, StringComparer.OrdinalIgnoreCase)))
			.Select(e => e.RoutePattern.RawText)
			.ToList();
		offending.ShouldBeEmpty();

		var a = await UploadAsync(s.Coach, s.Hierarchy.MatchId, 81, ct);
		foreach (var method in new[] { HttpMethod.Put, HttpMethod.Patch, HttpMethod.Delete })
		{
			foreach (var path in new[] { $"recording-versions/{a.Version}", $"recording-versions/{a.Version}/timeline-mappings", $"timeline-mappings/{a.Mapping}" })
			{
				using var response = await s.Admin.SendAsync(method, $"/api/v1/matches/{s.Hierarchy.MatchId}/{path}", null, ct);
				response.StatusCode.ShouldBeOneOf(HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed);
			}
		}
	}

	[Fact]
	public async Task ImmutableTablesRejectUpdateAndDeleteAndSessionVersionAdvances()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var db = await postgres.CreateDatabaseAsync(ct);
		(await MigratorHarness.RunAsync(db, TestMigrationCatalogs.Platform(), ct)).ExitCode.ShouldBe(MigratorExitCode.Success);
		await using var factory = new PlatformApiFactory(db, Config);
		await factory.WaitUntilHealthyAsync(ct);

		var accountId = await TestMembers.SeedAsync(db, "rec-admin", clubRoles: ["club-admin"], cancellationToken: ct);
		using var admin = await ApiSession.SignInAsync(factory, "rec-admin", TestMembers.DefaultPassword, ct);
		var hierarchy = await new ClubHierarchyBuilder(factory, admin).CreateAsync(ct);

		var sessionId = Guid.NewGuid();
		var versionId = Guid.NewGuid();
		var mappingId = Guid.NewGuid();
		var setId = Guid.NewGuid();
		var eventId = Guid.NewGuid();
		var hex = new string('a', 64);
		var now = DateTimeOffset.UtcNow;

		var inserts = new (string Sql, string Table)[]
		{
			("INSERT INTO socalytics.recording_upload_sessions (id, match_id, team_id, state, display_name, content_type, total_size_bytes, part_size_bytes, part_count, part_digests, content_digest, object_key, multipart_upload_id, created_by, created_at, expires_at, completed_at) " +
			 $"VALUES (@session, @match, @team, 'completed', 'r', 'video/mp4', 10, 10, 1, decode('{new string('a', 64)}', 'hex'), 'sha-256-parts:10:1:{hex}', 'k', 'u', @acct, @now, @now, @now)", "recording_upload_sessions"),
			("INSERT INTO socalytics.recording_versions (id, match_id, team_id, upload_session_id, object_key, total_size_bytes, part_size_bytes, part_count, content_digest, display_name, content_type, created_by, created_at) " +
			 $"VALUES (@version, @match, @team, @session, 'k', 10, 10, 1, 'sha-256-parts:10:1:{hex}', 'r', 'video/mp4', @acct, @now)", "recording_versions"),
			("INSERT INTO socalytics.recording_timeline_mappings (id, recording_version_id, match_id, spans, mapping_digest, created_by, created_at) " +
			 $"VALUES (@mapping, @version, @match, '[]', 'sha-256:{hex}', @acct, @now)", "recording_timeline_mappings"),
			("INSERT INTO socalytics.recording_set_versions (id, match_id, team_id, member_count, created_by, finalized_at) VALUES (@set, @match, @team, 1, @acct, @now)", "recording_set_versions"),
			("INSERT INTO socalytics.recording_set_members (recording_set_version_id, position, match_id, recording_version_id, timeline_mapping_id) VALUES (@set, 1, @match, @version, @mapping)", "recording_set_members"),
			("INSERT INTO socalytics.recording_retry_outcomes (operation, match_id, idempotency_key, request_digest, status_code, result, created_by, created_at) " +
			 $"VALUES ('start-upload', @match, 'key', 'sha-256:{hex}', 201, '{{}}', @acct, @now)", "recording_retry_outcomes"),
			("INSERT INTO socalytics.recording_finalized_events (event_id, event_type, contract_version, recording_set_version_id, match_id, team_id, occurred_at, payload) " +
			 "VALUES (@event, 'matches.recordings-finalized', '1.0.0', @set, @match, @team, @now, '{}')", "recording_finalized_events"),
		};

		void Bind(NpgsqlParameterCollection p)
		{
			p.AddWithValue("session", sessionId);
			p.AddWithValue("version", versionId);
			p.AddWithValue("mapping", mappingId);
			p.AddWithValue("set", setId);
			p.AddWithValue("event", eventId);
			p.AddWithValue("match", hierarchy.MatchId);
			p.AddWithValue("team", hierarchy.TeamId);
			p.AddWithValue("acct", accountId);
			p.AddWithValue("now", now);
		}

		foreach (var (sql, _) in inserts)
		{
			await using var c = new NpgsqlConnection(db.MigratorConnectionString);
			await c.OpenAsync(ct);
			await using var cmd = new NpgsqlCommand(sql, c);
			Bind(cmd.Parameters);
			await cmd.ExecuteNonQueryAsync(ct);
		}

		foreach (var table in ImmutableTables)
		{
			foreach (var statement in new[] { $"UPDATE socalytics.{table} SET created_by = created_by", $"DELETE FROM socalytics.{table}" })
			{
				var sql = table switch
				{
					"recording_set_versions" => statement.Replace("created_by = created_by", "member_count = member_count"),
					"recording_set_members" => statement.Replace("created_by = created_by", "position = position"),
					"recording_finalized_events" => statement.Replace("created_by = created_by", "team_id = team_id"),
					_ => statement,
				};

				var denied = await Should.ThrowAsync<PostgresException>(() => ExecAsync(db.AppConnectionString, sql, null, ct));
				denied.SqlState.ShouldBe("42501");
				var rejected = await Should.ThrowAsync<PostgresException>(() => ExecAsync(db.MigratorConnectionString, sql, null, ct));
				rejected.MessageText.ShouldContain("immutable");
			}

			await using var c = new NpgsqlConnection(db.MigratorConnectionString);
			await c.OpenAsync(ct);
			await using var count = new NpgsqlCommand($"SELECT count(*) FROM socalytics.{table}", c);
			((long)(await count.ExecuteScalarAsync(ct))!).ShouldBe(1);
		}

		await ExecAsync(db.AppConnectionString, "UPDATE socalytics.recording_upload_sessions SET description = 'changed' WHERE id = @id", p => p.AddWithValue("id", sessionId), ct);
		await using var vc = new NpgsqlConnection(db.MigratorConnectionString);
		await vc.OpenAsync(ct);
		await using var q = new NpgsqlCommand("SELECT version FROM socalytics.recording_upload_sessions WHERE id = @id", vc);
		q.Parameters.AddWithValue("id", sessionId);
		((long)(await q.ExecuteScalarAsync(ct))!).ShouldBe(2);
	}
}
