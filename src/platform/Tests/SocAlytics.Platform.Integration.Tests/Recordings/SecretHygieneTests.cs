using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using SocAlytics.Platform.Application.Recordings.Commands;
using SocAlytics.Platform.Integration.Tests.IdentityAccess.Support;
using SocAlytics.Platform.Integration.Tests.Infrastructure;
using SocAlytics.Platform.Integration.Tests.Recordings.Support;
using SocAlytics.Platform.Migrator;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.Recordings;

public sealed class SecretHygieneTests(PostgresContainerFixture postgres, RustFsContainerFixture rustFs)
{
	private static Task<HttpResponseMessage> PostAsync(ApiSession session, string path, object body, CancellationToken ct)
	{
		var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
		request.Headers.Add("X-CSRF-Token", session.AntiforgeryToken);
		request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
		return session.Client.SendAsync(request, ct);
	}

	private static async Task<List<string>> QueryAsync(IsolatedDatabase db, string sql, CancellationToken ct)
	{
		var rows = new List<string>();
		await using var connection = new NpgsqlConnection(db.MigratorConnectionString);
		await connection.OpenAsync(ct);
		await using var command = new NpgsqlCommand(sql, connection);
		await using var reader = await command.ExecuteReaderAsync(ct);
		while (await reader.ReadAsync(ct))
		{
			rows.Add(reader.GetString(0));
		}

		return rows;
	}

	[Fact]
	// FR-029, SC-008, R17
	public async Task NoSecretsReachLogsPersistedRecordingDataAuditOrRetryOutcomes()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var db = await postgres.CreateDatabaseAsync(ct);
		(await MigratorHarness.RunAsync(db, TestMigrationCatalogs.Platform(), ct)).ExitCode.ShouldBe(MigratorExitCode.Success);
		await using var factory = new PlatformApiFactory(
			db,
			recordingUpload: RecordingTestData.WithKeyPrefix(RecordingTestData.NewKeyPrefix(), new Dictionary<string, string?> { ["SessionLifetime"] = "00:00:05", ["ExpirySweepInterval"] = "01:00:00" }),
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
		var admin = await ApiSession.SignInAsync(factory, "admin-1", TestMembers.DefaultPassword, ct);
		var match = (await new ClubHierarchyBuilder(factory, admin).CreateAsync(ct)).MatchId;
		var spans = new[] { new { mediaStartSeconds = 0, mediaEndSeconds = 10, matchStartSeconds = 0 } };

		var recording = RecordingTestData.Generate(1, 2048);
		using var started = await PostAsync(admin, $"/api/v1/matches/{match}/upload-sessions", recording.Declaration(), ct);
		started.StatusCode.ShouldBe(HttpStatusCode.Created, await started.Content.ReadAsStringAsync(ct));
		started.Headers.CacheControl!.NoStore.ShouldBeTrue();
		var body = await started.Content.ReadFromJsonAsync<JsonElement>(ct);
		var id = body.GetProperty("session").GetProperty("id").GetString();
		using (var storage = new HttpClient())
		{
			using var put = await RecordingTestData.PutPartAsync(storage, body.GetProperty("grants").GetProperty("parts")[0], recording.Parts[0], ct);
			put.IsSuccessStatusCode.ShouldBeTrue();
		}

		using var regrant = await PostAsync(admin, $"/api/v1/matches/{match}/upload-sessions/{id}/grants", new { partNumbers = new[] { 1 } }, ct);
		regrant.StatusCode.ShouldBe(HttpStatusCode.OK, await regrant.Content.ReadAsStringAsync(ct));
		regrant.Headers.CacheControl!.NoStore.ShouldBeTrue();

		using var completed = await PostAsync(admin, $"/api/v1/matches/{match}/upload-sessions/{id}/completion", new { timelineMapping = new { spans } }, ct);
		completed.StatusCode.ShouldBe(HttpStatusCode.Created, await completed.Content.ReadAsStringAsync(ct));
		var result = await completed.Content.ReadFromJsonAsync<JsonElement>(ct);
		var version = result.GetProperty("recordingVersion").GetProperty("id").GetGuid();

		using var revised = await PostAsync(admin, $"/api/v1/matches/{match}/recording-versions/{version}/timeline-mappings",
			new { spans = new[] { new { mediaStartSeconds = 0, mediaEndSeconds = 10, matchStartSeconds = 7 } } }, ct);
		revised.StatusCode.ShouldBe(HttpStatusCode.Created, await revised.Content.ReadAsStringAsync(ct));
		var revisedMapping = (await revised.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("id").GetGuid();

		using var finalized = await PostAsync(admin, $"/api/v1/matches/{match}/recording-set-versions",
			new { members = new[] { new { recordingVersionId = version, timelineMappingId = revisedMapping } } }, ct);
		finalized.StatusCode.ShouldBe(HttpStatusCode.Created, await finalized.Content.ReadAsStringAsync(ct));

		var second = RecordingTestData.Generate(2, 2048);
		using var pending = await PostAsync(admin, $"/api/v1/matches/{match}/upload-sessions", second.Declaration(), ct);
		pending.StatusCode.ShouldBe(HttpStatusCode.Created, await pending.Content.ReadAsStringAsync(ct));
		await Task.Delay(TimeSpan.FromSeconds(6), ct);
		await using (var scope = factory.Services.CreateAsyncScope())
		{
			await scope.ServiceProvider.GetRequiredService<ExpireUploadSessionsHandler>().HandleAsync(ct);
		}

		var objectKeys = await QueryAsync(db, "SELECT object_key FROM socalytics.recording_upload_sessions", ct);
		var uploadIds = await QueryAsync(db, "SELECT multipart_upload_id FROM socalytics.recording_upload_sessions", ct);
		objectKeys.Count.ShouldBe(2);
		var forbidden = new List<string> { "X-Amz-Signature", "X-Amz-Credential", rustFs.SecretKey, rustFs.AccessKey };
		forbidden.AddRange(objectKeys);
		forbidden.AddRange(uploadIds);

		var haystacks = new List<(string Source, string Text)>();
		foreach (var entry in factory.Logs.Entries)
		{
			haystacks.Add(("log", $"{entry.Category} {entry.Message} {entry.Exception} {string.Join(' ', entry.State.Select(p => $"{p.Key}={p.Value}"))}"));
		}

		var tables = await QueryAsync(db, "SELECT tablename FROM pg_tables WHERE schemaname = 'socalytics' AND tablename LIKE 'recording\\_%'", ct);
		tables.ShouldNotBeEmpty();
		foreach (var table in tables)
		{
			// Upload session rows legitimately own their object key and upload id; every other column is scanned.
			foreach (var row in await QueryAsync(db, $"SELECT (to_jsonb(t) - 'object_key' - 'multipart_upload_id')::text FROM socalytics.{table} t", ct))
			{
				haystacks.Add((table, row));
			}
		}

		foreach (var row in await QueryAsync(db, "SELECT to_jsonb(e)::text FROM socalytics.security_audit_event e", ct))
		{
			haystacks.Add(("security_audit_event", row));
		}

		foreach (var row in await QueryAsync(db, "SELECT result::text FROM socalytics.recording_retry_outcomes", ct))
		{
			haystacks.Add(("retry outcome", row));
		}

		foreach (var (source, text) in haystacks)
		{
			foreach (var secret in forbidden)
			{
				text.Contains(secret, StringComparison.OrdinalIgnoreCase).ShouldBeFalse($"{source} leaks a secret value ({secret.Length} chars)");
			}
		}
	}
}
