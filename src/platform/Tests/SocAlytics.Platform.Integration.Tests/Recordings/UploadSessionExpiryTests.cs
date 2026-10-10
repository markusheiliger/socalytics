using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Amazon.S3;
using Amazon.S3.Model;
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

public sealed class UploadSessionExpiryTests(PostgresContainerFixture postgres, RustFsContainerFixture rustFs)
{
	private static async Task<long> ScalarAsync(IsolatedDatabase db, string sql, CancellationToken ct)
	{
		await using var connection = new NpgsqlConnection(db.MigratorConnectionString);
		await connection.OpenAsync(ct);
		await using var command = new NpgsqlCommand(sql, connection);
		return Convert.ToInt64(await command.ExecuteScalarAsync(ct));
	}

	[Fact]
	// FR-034, SC-010, REC-R06, FR-030
	public async Task ExpiredSessionIsRefusedImmediatelyAndReleasedBySweep()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var db = await postgres.CreateDatabaseAsync(ct);
		(await MigratorHarness.RunAsync(db, TestMigrationCatalogs.Platform(), ct)).ExitCode.ShouldBe(MigratorExitCode.Success);
		await using var factory = new PlatformApiFactory(
			db,
			recordingUpload: new Dictionary<string, string?> { ["SessionLifetime"] = "00:00:02", ["ExpirySweepInterval"] = "01:00:00" },
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
		var hierarchy = await new ClubHierarchyBuilder(factory, admin).CreateAsync(ct);

		var recording = RecordingTestData.Generate(1, 5_242_880, 5_242_880);
		using var start = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/matches/{hierarchy.MatchId}/upload-sessions") { Content = JsonContent.Create(recording.Declaration()) };
		start.Headers.Add("X-CSRF-Token", admin.AntiforgeryToken);
		start.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
		using var started = await admin.Client.SendAsync(start, ct);
		started.StatusCode.ShouldBe(HttpStatusCode.Created, await started.Content.ReadAsStringAsync(ct));
		var body = await started.Content.ReadFromJsonAsync<JsonElement>(ct);
		var id = body.GetProperty("session").GetProperty("id").GetString()!;
		var firstGrant = body.GetProperty("grants").GetProperty("parts")[0];
		using (var storage = new HttpClient())
		{
			using var put = await RecordingTestData.PutPartAsync(storage, firstGrant, recording.Parts[0], ct);
			put.IsSuccessStatusCode.ShouldBeTrue();
		}

		var sessionUrl = $"/api/v1/matches/{hierarchy.MatchId}/upload-sessions/{id}";
		using var before = await admin.SendAsync(HttpMethod.Get, sessionUrl, null, ct);
		before.Headers.ETag.ShouldNotBeNull();
		var etag = before.Headers.ETag!.Tag;

		await Task.Delay(TimeSpan.FromSeconds(3), ct);

		async Task<HttpResponseMessage> PostAsync(string path, object content)
		{
			using var request = new HttpRequestMessage(HttpMethod.Post, $"{sessionUrl}/{path}") { Content = JsonContent.Create(content) };
			request.Headers.Add("X-CSRF-Token", admin.AntiforgeryToken);
			request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
			return await admin.Client.SendAsync(request, ct);
		}

		using var grants = await PostAsync("grants", new { partNumbers = new[] { 1 } });
		grants.StatusCode.ShouldBe(HttpStatusCode.Conflict);
		(await grants.Content.ReadAsStringAsync(ct)).ShouldContain("upload-session-expired");
		using var completion = await PostAsync("completion", new { timelineMapping = new { spans = new[] { new { mediaStartSeconds = 0, mediaEndSeconds = 10, matchStartSeconds = 5 } } } });
		completion.StatusCode.ShouldBe(HttpStatusCode.Conflict);
		(await completion.Content.ReadAsStringAsync(ct)).ShouldContain("upload-session-expired");

		using var reported = await admin.SendAsync(HttpMethod.Get, sessionUrl, null, ct);
		(await reported.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("state").GetString().ShouldBe("expired");
		reported.Headers.ETag!.Tag.ShouldBe(etag);

		for (var sweep = 0; sweep < 2; sweep++)
		{
			await using var scope = factory.Services.CreateAsyncScope();
			await scope.ServiceProvider.GetRequiredService<ExpireUploadSessionsHandler>().HandleAsync(ct);
		}

		using var after = await admin.SendAsync(HttpMethod.Get, sessionUrl, null, ct);
		(await after.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("state").GetString().ShouldBe("expired");
		(await ScalarAsync(db, "SELECT count(*) FROM socalytics.recording_upload_sessions WHERE storage_released_at IS NOT NULL", ct)).ShouldBe(1);
		(await ScalarAsync(db, "SELECT count(*) FROM socalytics.recording_versions", ct)).ShouldBe(0);
		(await ScalarAsync(db, $"SELECT count(*) FROM socalytics.security_audit_event WHERE event_type = 'recording.upload.expire' AND resource_id = '{id}' AND actor_kind = 'system' AND resource_type = 'upload-session'", ct)).ShouldBe(1);

		string objectKey;
		string uploadId;
		await using (var connection = new NpgsqlConnection(db.MigratorConnectionString))
		{
			await connection.OpenAsync(ct);
			await using var command = new NpgsqlCommand($"SELECT object_key, multipart_upload_id FROM socalytics.recording_upload_sessions WHERE id = '{id}'", connection);
			await using var reader = await command.ExecuteReaderAsync(ct);
			(await reader.ReadAsync(ct)).ShouldBeTrue();
			objectKey = reader.GetString(0);
			uploadId = reader.GetString(1);
		}

		using var s3 = rustFs.CreateS3Client();
		var listParts = await Should.ThrowAsync<AmazonS3Exception>(() =>
			s3.ListPartsAsync(new ListPartsRequest { BucketName = rustFs.Bucket, Key = objectKey, UploadId = uploadId }, ct));
		listParts.ErrorCode.ShouldBe("NoSuchUpload");
		var head = await Should.ThrowAsync<AmazonS3Exception>(() =>
			s3.GetObjectMetadataAsync(new GetObjectMetadataRequest { BucketName = rustFs.Bucket, Key = objectKey }, ct));
		head.StatusCode.ShouldBe(HttpStatusCode.NotFound);
	}
}
