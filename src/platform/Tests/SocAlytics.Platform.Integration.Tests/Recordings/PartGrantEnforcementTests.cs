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

public sealed class PartGrantEnforcementTests(PostgresContainerFixture postgres, RustFsContainerFixture rustFs)
{
	private sealed record Setup(IsolatedDatabase Db, PlatformApiFactory Factory, ApiSession Coach, Guid CoachId, ClubHierarchy Hierarchy);

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
		return new Setup(db, factory, coach, coachId, hierarchy);
	}

	private static async Task<string> StartUploadAsync(Setup s, TestRecording recording, CancellationToken ct)
	{
		using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/matches/{s.Hierarchy.MatchId}/upload-sessions")
		{
			Content = JsonContent.Create(recording.Declaration()),
		};
		request.Headers.Add("X-CSRF-Token", s.Coach.AntiforgeryToken);
		request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
		using var response = await s.Coach.Client.SendAsync(request, ct);
		response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(ct));
		var body = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
		return body.GetProperty("session").GetProperty("id").GetString()!;
	}

	private static Task<HttpResponseMessage> GrantsAsync(Setup s, string sessionId, object body, CancellationToken ct, string? traceId = null, Guid? matchId = null)
	{
		var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/matches/{matchId ?? s.Hierarchy.MatchId}/upload-sessions/{sessionId}/grants")
		{
			Content = JsonContent.Create(body),
		};
		request.Headers.Add("X-CSRF-Token", s.Coach.AntiforgeryToken);
		if (traceId is not null)
		{
			request.Headers.Add("traceparent", $"00-{traceId}-0123456789abcdef-01");
		}

		return s.Coach.Client.SendAsync(request, ct);
	}

	private async Task<IReadOnlyList<PartDetail>> StoredPartsAsync(Guid matchId, CancellationToken ct)
	{
		using var s3 = rustFs.CreateS3Client();
		var uploads = await s3.ListMultipartUploadsAsync(new ListMultipartUploadsRequest { BucketName = rustFs.Bucket }, ct);
		var upload = uploads.MultipartUploads!.Single(u => u.Key.Contains(matchId.ToString(), StringComparison.Ordinal));
		var parts = await s3.ListPartsAsync(new ListPartsRequest { BucketName = rustFs.Bucket, Key = upload.Key, UploadId = upload.UploadId }, ct);
		return parts.Parts ?? [];
	}

	private static async Task<bool> Accepted(Func<Task<HttpResponseMessage>> send)
	{
		try
		{
			using var r = await send();
			return r.IsSuccessStatusCode;
		}
		catch (HttpRequestException)
		{
			return false;
		}
	}

	[Fact]
	// US1 AS8, FR-030
	public async Task FreshGrantsTargetTheSamePartsAndAreAudited()
	{
		var ct = TestContext.Current.CancellationToken;
		var s = await StartAsync(ct);
		await using var db0 = s.Db;
		await using var fac0 = s.Factory;
		var recording = RecordingTestData.Generate(10, 5_242_880, 5_242_880, 1000);
		var sessionId = await StartUploadAsync(s, recording, ct);
		var traceId = Guid.NewGuid().ToString("N");

		using var response = await GrantsAsync(s, sessionId, new { partNumbers = new[] { 3, 1 } }, ct, traceId);
		response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(ct));
		response.Headers.CacheControl!.NoStore.ShouldBeTrue();
		var parts = (await response.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("parts");
		parts.EnumerateArray().Select(p => p.GetProperty("partNumber").GetInt32()).ShouldBe([3, 1]);
		var path = new Uri(parts[0].GetProperty("url").GetString()!).AbsolutePath;
		path.ShouldContain($"/matches/{s.Hierarchy.MatchId}/upload-sessions/{sessionId}");

		using var storage = new HttpClient();
		using var put = await RecordingTestData.PutPartAsync(storage, parts[1], recording.Parts[0], ct);
		put.IsSuccessStatusCode.ShouldBeTrue(await put.Content.ReadAsStringAsync(ct));

		await RecordingAuditAssertions.AssertSingleAsync(
			s.Db, "recording.upload.grant", sessionId, s.CoachId, s.Hierarchy.TeamId, s.Hierarchy.MatchId, "succeeded", traceId,
			new HashSet<string> { "matchId", "grantedPartCount", "grantExpiresAt" }, ct);
		await RecordingAuditAssertions.AssertNoGrantUrlAsync(s.Db, ct);
	}

	[Fact]
	public async Task InvalidPartNumbersAndForeignSessionsAreRefused()
	{
		var ct = TestContext.Current.CancellationToken;
		var s = await StartAsync(ct);
		await using var db0 = s.Db;
		await using var fac0 = s.Factory;
		var sessionId = await StartUploadAsync(s, RecordingTestData.Generate(11, 5_242_880, 10), ct);

		foreach (var numbers in new[] { new[] { 0 }, new[] { 3 }, new[] { 1, 1 }, Array.Empty<int>() })
		{
			using var response = await GrantsAsync(s, sessionId, new { partNumbers = numbers }, ct);
			response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
			(await response.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("code").GetString().ShouldBe("part-numbers-invalid");
		}

		using var unknown = await GrantsAsync(s, Guid.NewGuid().ToString(), new { partNumbers = new[] { 1 } }, ct);
		unknown.StatusCode.ShouldBe(HttpStatusCode.NotFound);
		using var foreign = await GrantsAsync(s, sessionId, new { partNumbers = new[] { 1 } }, ct, matchId: Guid.NewGuid());
		foreign.StatusCode.ShouldBe(HttpStatusCode.NotFound);
	}

	[Fact]
	// SC-004, edge "Grant misuse"
	public async Task MisusedGrantsAreRefusedByStorage()
	{
		var ct = TestContext.Current.CancellationToken;
		var s = await StartAsync(ct);
		await using var db0 = s.Db;
		await using var fac0 = s.Factory;
		var recording = RecordingTestData.Generate(12, 5_242_880, 5_242_880, 1000);
		var sessionId = await StartUploadAsync(s, recording, ct);
		using var response = await GrantsAsync(s, sessionId, new { partNumbers = new[] { 1, 2 } }, ct);
		var parts = (await response.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("parts");
		var grant = parts[0];
		using var storage = new HttpClient();

		var flipped = (byte[])recording.Parts[0].Clone();
		flipped[0] ^= 0xFF;
		(await Accepted(() => RecordingTestData.PutPartAsync(storage, grant, flipped, ct))).ShouldBeFalse();

		(await Accepted(() => RecordingTestData.PutPartAsync(storage, grant, recording.Parts[0][..^1], ct))).ShouldBeFalse();

		(await Accepted(() => RecordingTestData.PutPartAsync(storage, grant, [.. recording.Parts[0], 0], ct))).ShouldBeFalse();

		var url = grant.GetProperty("url").GetString()!;
		(await Accepted(() => storage.SendAsync(new HttpRequestMessage(HttpMethod.Put, url.Replace("partNumber=1", "partNumber=2", StringComparison.Ordinal)) { Content = new ByteArrayContent(recording.Parts[0]) }, ct))).ShouldBeFalse();

		(await Accepted(() => storage.SendAsync(new HttpRequestMessage(HttpMethod.Put, url) { Content = new ByteArrayContent(recording.Parts[0]) }, ct))).ShouldBeFalse();

		(await StoredPartsAsync(s.Hierarchy.MatchId, ct)).ShouldBeEmpty();

		foreach (var method in new[] { HttpMethod.Get, HttpMethod.Delete, HttpMethod.Post, HttpMethod.Head })
		{
			(await Accepted(() => storage.SendAsync(new HttpRequestMessage(method, url), ct))).ShouldBeFalse();
		}

		var other = url.Replace(new Uri(url).AbsolutePath, new Uri(url).AbsolutePath + "-other", StringComparison.Ordinal);
		(await Accepted(() => storage.SendAsync(new HttpRequestMessage(HttpMethod.Put, other) { Content = new ByteArrayContent(recording.Parts[0]) }, ct))).ShouldBeFalse();

		(await StoredPartsAsync(s.Hierarchy.MatchId, ct)).ShouldBeEmpty();
	}
}
