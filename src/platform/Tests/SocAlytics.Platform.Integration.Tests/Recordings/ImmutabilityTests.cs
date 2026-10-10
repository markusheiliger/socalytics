using Npgsql;
using Shouldly;
using SocAlytics.Platform.Integration.Tests.IdentityAccess.Support;
using SocAlytics.Platform.Integration.Tests.Infrastructure;
using SocAlytics.Platform.Integration.Tests.Recordings.Support;
using SocAlytics.Platform.Migrator;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.Recordings;

public sealed class ImmutabilityTests(PostgresContainerFixture postgres)
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
