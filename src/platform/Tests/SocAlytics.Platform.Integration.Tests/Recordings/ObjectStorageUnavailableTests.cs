using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;
using Shouldly;
using SocAlytics.Platform.Integration.Tests.IdentityAccess.Support;
using SocAlytics.Platform.Integration.Tests.Infrastructure;
using SocAlytics.Platform.Integration.Tests.Recordings.Support;
using SocAlytics.Platform.Migrator;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.Recordings;

public sealed class ObjectStorageUnavailableTests(PostgresContainerFixture postgres, RustFsContainerFixture rustFs)
{
	private static readonly object ValidMapping = new { timelineMapping = new { spans = new[] { new { mediaStartSeconds = 0, mediaEndSeconds = 10, matchStartSeconds = 5 } } } };

	private static async Task<HttpResponseMessage> PostAsync(ApiSession session, string url, object body, string? key, CancellationToken ct)
	{
		using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
		request.Headers.Add("X-CSRF-Token", session.AntiforgeryToken);
		if (key is not null)
		{
			request.Headers.Add("Idempotency-Key", key);
		}

		return await session.Client.SendAsync(request, ct);
	}

	private static async Task<string> StateSnapshotAsync(IsolatedDatabase db, CancellationToken ct)
	{
		await using var connection = new NpgsqlConnection(db.MigratorConnectionString);
		await connection.OpenAsync(ct);
		await using var command = new NpgsqlCommand(
			"SELECT (SELECT count(*) FROM socalytics.recording_upload_sessions) || '/' || " +
			"(SELECT count(*) FROM socalytics.recording_upload_sessions WHERE state = 'pending') || '/' || " +
			"(SELECT count(*) FROM socalytics.recording_versions) || '/' || " +
			"(SELECT count(*) FROM socalytics.recording_retry_outcomes) || '/' || " +
			"(SELECT count(*) FROM socalytics.security_audit_event WHERE event_type LIKE 'recording.%')", connection);
		return (string)(await command.ExecuteScalarAsync(ct))!;
	}

	private static async Task AssertUnavailableAsync(HttpResponseMessage response, CancellationToken ct)
	{
		var text = await response.Content.ReadAsStringAsync(ct);
		response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable, text);
		using var json = JsonDocument.Parse(text);
		json.RootElement.GetProperty("code").GetString().ShouldBe("object-storage-unavailable");
		json.RootElement.GetProperty("status").GetInt32().ShouldBe(503);
	}

	[Fact]
	// Edge "Object storage unavailable", FR-026, R8
	public async Task UnavailableStorageFailsWithoutRecordingStateAndHealthyRetriesSucceed()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var db = await postgres.CreateDatabaseAsync(ct);
		(await MigratorHarness.RunAsync(db, TestMigrationCatalogs.Platform(), ct)).ExitCode.ShouldBe(MigratorExitCode.Success);

		Dictionary<string, string?> Storage(string url) => new()
		{
			["ServiceUrl"] = url,
			["Region"] = rustFs.Region,
			["AccessKey"] = rustFs.AccessKey,
			["SecretKey"] = rustFs.SecretKey,
			["Bucket"] = rustFs.Bucket,
		};

		await using var healthy = new PlatformApiFactory(db, objectStorage: Storage(rustFs.ServiceUrl));
		await using var unavailable = new PlatformApiFactory(db, objectStorage: Storage("http://127.0.0.1:9"));
		healthy.Time.Set(DateTimeOffset.UtcNow);
		unavailable.Time.Set(DateTimeOffset.UtcNow);

		await TestMembers.SeedAsync(db, "admin-1", clubRoles: ["club-admin"], cancellationToken: ct);
		var coachId = await TestMembers.SeedAsync(db, "coach-1", cancellationToken: ct);
		var admin = await ApiSession.SignInAsync(healthy, "admin-1", TestMembers.DefaultPassword, ct);
		var hierarchy = await new ClubHierarchyBuilder(healthy, admin).CreateAsync(ct);
		await using (var c = new NpgsqlConnection(db.AppConnectionString))
		{
			await c.OpenAsync(ct);
			await using var cmd = new NpgsqlCommand(
				"INSERT INTO socalytics.team_role_assignment (member_account_id, team_id, role, assigned_at, assigned_by_account_id) VALUES (@m, @t, 'coach', now(), @m)", c);
			cmd.Parameters.AddWithValue("m", coachId);
			cmd.Parameters.AddWithValue("t", hierarchy.TeamId);
			await cmd.ExecuteNonQueryAsync(ct);
		}

		var coach = await ApiSession.SignInAsync(healthy, "coach-1", TestMembers.DefaultPassword, ct);
		var coachOnUnavailable = await ApiSession.SignInAsync(unavailable, "coach-1", TestMembers.DefaultPassword, ct);

		var recording = RecordingTestData.Generate(31, 1024);
		var other = RecordingTestData.Generate(32, 2048);
		var matchUrl = $"/api/v1/matches/{hierarchy.MatchId}/upload-sessions";

		using var started = await PostAsync(coach, matchUrl, recording.Declaration(), Guid.NewGuid().ToString(), ct);
		started.StatusCode.ShouldBe(HttpStatusCode.Created, await started.Content.ReadAsStringAsync(ct));
		var startedBody = await started.Content.ReadFromJsonAsync<JsonElement>(ct);
		var id = startedBody.GetProperty("session").GetProperty("id").GetString()!;
		using (var storage = new HttpClient())
		{
			var i = 0;
			foreach (var grant in startedBody.GetProperty("grants").GetProperty("parts").EnumerateArray())
			{
				using var put = await RecordingTestData.PutPartAsync(storage, grant, recording.Parts[i++], ct);
				put.IsSuccessStatusCode.ShouldBeTrue(await put.Content.ReadAsStringAsync(ct));
			}
		}

		var before = await StateSnapshotAsync(db, ct);
		var startKey = Guid.NewGuid().ToString();
		var completeKey = Guid.NewGuid().ToString();

		using (var failedStart = await PostAsync(coachOnUnavailable, matchUrl, other.Declaration(), startKey, ct))
		{
			await AssertUnavailableAsync(failedStart, ct);
		}

		using (var failedGrants = await PostAsync(coachOnUnavailable, $"{matchUrl}/{id}/grants", new { partNumbers = new[] { 1 } }, null, ct))
		{
			await AssertUnavailableAsync(failedGrants, ct);
		}

		using (var failedCompletion = await PostAsync(coachOnUnavailable, $"{matchUrl}/{id}/completion", ValidMapping, completeKey, ct))
		{
			await AssertUnavailableAsync(failedCompletion, ct);
		}

		(await StateSnapshotAsync(db, ct)).ShouldBe(before);

		using var retriedStart = await PostAsync(coach, matchUrl, other.Declaration(), startKey, ct);
		retriedStart.StatusCode.ShouldBe(HttpStatusCode.Created, await retriedStart.Content.ReadAsStringAsync(ct));
		using var retriedGrants = await PostAsync(coach, $"{matchUrl}/{id}/grants", new { partNumbers = new[] { 1 } }, null, ct);
		retriedGrants.StatusCode.ShouldBe(HttpStatusCode.OK, await retriedGrants.Content.ReadAsStringAsync(ct));
		using var retriedCompletion = await PostAsync(coach, $"{matchUrl}/{id}/completion", ValidMapping, completeKey, ct);
		retriedCompletion.StatusCode.ShouldBe(HttpStatusCode.Created, await retriedCompletion.Content.ReadAsStringAsync(ct));
	}
}
