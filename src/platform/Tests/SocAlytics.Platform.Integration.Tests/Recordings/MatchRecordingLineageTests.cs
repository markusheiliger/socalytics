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

// US4 single-resource reads, FR-027
public sealed class MatchRecordingLineageTests(PostgresContainerFixture postgres, RustFsContainerFixture rustFs)
{
	private static Task<HttpResponseMessage> PostAsync(ApiSession session, string path, object body, CancellationToken ct)
	{
		var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
		request.Headers.Add("X-CSRF-Token", session.AntiforgeryToken);
		request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
		return session.Client.SendAsync(request, ct);
	}

	private static object Spans(int matchStart) =>
		new { spans = new[] { new { mediaStartSeconds = 0, mediaEndSeconds = 10, matchStartSeconds = matchStart } } };

	private static async Task<(Guid Version, Guid Mapping, string VersionLocation, string MappingLocation)> UploadAsync(
		ApiSession session, Guid matchId, int seed, CancellationToken ct)
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
		using var completed = await PostAsync(session, $"/api/v1/matches/{matchId}/upload-sessions/{id}/completion", new { timelineMapping = Spans(0) }, ct);
		completed.StatusCode.ShouldBe(HttpStatusCode.Created, await completed.Content.ReadAsStringAsync(ct));
		var result = await completed.Content.ReadFromJsonAsync<JsonElement>(ct);
		var version = result.GetProperty("recordingVersion").GetProperty("id").GetGuid();
		var mapping = result.GetProperty("timelineMapping").GetProperty("id").GetGuid();
		return (version, mapping, completed.Headers.Location!.ToString(), $"/api/v1/matches/{matchId}/timeline-mappings/{mapping}");
	}

	private static async Task<ApiSession> MemberAsync(FinalizationSetup s, string name, string? role, CancellationToken ct)
	{
		var id = await TestMembers.SeedAsync(s.Db, name, cancellationToken: ct);
		if (role is not null)
		{
			await using var c = new NpgsqlConnection(s.Db.AppConnectionString);
			await c.OpenAsync(ct);
			await using var cmd = new NpgsqlCommand(
				"INSERT INTO socalytics.team_role_assignment (member_account_id, team_id, role, assigned_at, assigned_by_account_id) VALUES (@m, @t, @r, now(), @m)", c);
			cmd.Parameters.AddWithValue("m", id);
			cmd.Parameters.AddWithValue("t", s.Hierarchy.TeamId);
			cmd.Parameters.AddWithValue("r", role);
			await cmd.ExecuteNonQueryAsync(ct);
		}

		return await ApiSession.SignInAsync(s.Factory, name, TestMembers.DefaultPassword, ct);
	}

	[Fact]
	// US4 single resources: Viewer, Coach, Club Admin read; Location values resolve; no storage details leak
	public async Task SingleResourcesAreReadableWithoutStorageDetails()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var s = await FinalizationSetup.StartAsync(postgres, rustFs, ct);
		var match = s.Hierarchy.MatchId;
		var up = await UploadAsync(s.Coach, match, 1, ct);
		using var revised = await PostAsync(s.Coach, $"/api/v1/matches/{match}/recording-versions/{up.Version}/timeline-mappings", Spans(7), ct);
		revised.StatusCode.ShouldBe(HttpStatusCode.Created, await revised.Content.ReadAsStringAsync(ct));
		var revisedLocation = revised.Headers.Location!.ToString();
		var viewer = await MemberAsync(s, "viewer-1", "viewer", ct);

		foreach (var session in new[] { viewer, s.Coach, s.Admin })
		{
			using var version = await session.Client.GetAsync(up.VersionLocation, ct);
			version.StatusCode.ShouldBe(HttpStatusCode.OK, await version.Content.ReadAsStringAsync(ct));
			version.Headers.ETag!.Tag.ShouldBe($"\"{up.Version}\"");
			var text = await version.Content.ReadAsStringAsync(ct);
			JsonDocument.Parse(text).RootElement.GetProperty("id").GetGuid().ShouldBe(up.Version);
			AssertNoStorageDetails(text);

			foreach (var location in new[] { up.MappingLocation, revisedLocation })
			{
				using var mapping = await session.Client.GetAsync(location, ct);
				mapping.StatusCode.ShouldBe(HttpStatusCode.OK, await mapping.Content.ReadAsStringAsync(ct));
				mapping.Headers.ETag.ShouldNotBeNull();
				var mappingText = await mapping.Content.ReadAsStringAsync(ct);
				JsonDocument.Parse(mappingText).RootElement.GetProperty("recordingVersionId").GetGuid().ShouldBe(up.Version);
				AssertNoStorageDetails(mappingText);
			}
		}
	}

	[Fact]
	// US4: other-Team member forbidden, foreign-Match resources not found
	public async Task ForeignAccessIsForbiddenAndForeignResourcesAreNotFound()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var s = await FinalizationSetup.StartAsync(postgres, rustFs, ct);
		var match = s.Hierarchy.MatchId;
		var up = await UploadAsync(s.Coach, match, 2, ct);
		var (_, otherMatch) = await new ClubHierarchyBuilder(s.Factory, s.Admin).CreateTeamWithMatchAsync(s.Hierarchy.SeasonId, ct);
		var other = await UploadAsync(s.Admin, otherMatch, 3, ct);
		var outsider = await MemberAsync(s, "outsider-1", null, ct);

		using (var forbidden = await outsider.Client.GetAsync(up.VersionLocation, ct))
		{
			forbidden.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
		}

		using (var forbidden = await outsider.Client.GetAsync(up.MappingLocation, ct))
		{
			forbidden.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
		}

		using (var missing = await s.Coach.Client.GetAsync($"/api/v1/matches/{match}/recording-versions/{other.Version}", ct))
		{
			missing.StatusCode.ShouldBe(HttpStatusCode.NotFound);
		}

		using (var missing = await s.Coach.Client.GetAsync($"/api/v1/matches/{match}/timeline-mappings/{other.Mapping}", ct))
		{
			missing.StatusCode.ShouldBe(HttpStatusCode.NotFound);
		}
	}

	private static async Task FinalizeAsync(ApiSession session, Guid matchId, (Guid Version, Guid Mapping)[] members, CancellationToken ct)
	{
		using var response = await PostAsync(
			session,
			$"/api/v1/matches/{matchId}/recording-set-versions",
			new { members = members.Select(m => new { recordingVersionId = m.Version, timelineMappingId = m.Mapping }).ToArray() },
			ct);
		response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(ct));
	}

	[Fact]
	// US4 AS1-AS2, FR-006, FR-027: lineage view, weak ETag, authorization, archived Season
	public async Task MatchLineageListsEverythingWithWeakETag()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var s = await FinalizationSetup.StartAsync(postgres, rustFs, ct);
		var match = s.Hierarchy.MatchId;
		var one = await UploadAsync(s.Coach, match, 11, ct);
		var two = await UploadAsync(s.Coach, match, 12, ct);
		using var revised = await PostAsync(s.Coach, $"/api/v1/matches/{match}/recording-versions/{one.Version}/timeline-mappings", Spans(7), ct);
		revised.StatusCode.ShouldBe(HttpStatusCode.Created, await revised.Content.ReadAsStringAsync(ct));
		await FinalizeAsync(s.Coach, match, [(one.Version, one.Mapping), (two.Version, two.Mapping)], ct);
		await FinalizeAsync(s.Coach, match, [(two.Version, two.Mapping), (one.Version, one.Mapping)], ct);

		// pending session must not appear as a recording version
		var pending = RecordingTestData.Generate(13, 2048);
		using (var started = await PostAsync(s.Coach, $"/api/v1/matches/{match}/upload-sessions", pending.Declaration(), ct))
		{
			started.StatusCode.ShouldBe(HttpStatusCode.Created);
		}

		var viewer = await MemberAsync(s, "viewer-2", "viewer", ct);
		var path = $"/api/v1/matches/{match}/recording-lineage";
		string etag;
		foreach (var session in new[] { viewer, s.Coach, s.Admin })
		{
			using var response = await session.Client.GetAsync(path, ct);
			response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(ct));
			response.Headers.ETag!.IsWeak.ShouldBeTrue();
			var text = await response.Content.ReadAsStringAsync(ct);
			AssertNoStorageDetails(text);
			var root = JsonDocument.Parse(text).RootElement;
			root.GetProperty("recordingVersions").GetArrayLength().ShouldBe(2);
			root.GetProperty("timelineMappings").GetArrayLength().ShouldBe(3);
			var sets = root.GetProperty("recordingSetVersions");
			sets.GetArrayLength().ShouldBe(2);
			foreach (var set in sets.EnumerateArray())
			{
				var members = set.GetProperty("members");
				members.GetArrayLength().ShouldBe(2);
				members[0].GetProperty("position").GetInt32().ShouldBe(1);
				members[0].GetProperty("recordingContentDigest").GetString().ShouldNotBeNullOrEmpty();
				members[0].GetProperty("timelineMappingDigest").GetString().ShouldNotBeNullOrEmpty();
			}
		}

		using (var first = await viewer.Client.GetAsync(path, ct))
		{
			etag = first.Headers.ETag!.ToString();
		}

		using (var cached = new HttpRequestMessage(HttpMethod.Get, path))
		{
			cached.Headers.TryAddWithoutValidation("If-None-Match", etag);
			using var response = await viewer.Client.SendAsync(cached, ct);
			response.StatusCode.ShouldBe(HttpStatusCode.NotModified);
		}

		await FinalizeAsync(s.Coach, match, [(one.Version, one.Mapping)], ct);
		using (var stale = new HttpRequestMessage(HttpMethod.Get, path))
		{
			stale.Headers.TryAddWithoutValidation("If-None-Match", etag);
			using var response = await viewer.Client.SendAsync(stale, ct);
			response.StatusCode.ShouldBe(HttpStatusCode.OK);
			response.Headers.ETag!.ToString().ShouldNotBe(etag);
		}

		var outsider = await MemberAsync(s, "outsider-2", null, ct);
		using (var forbidden = await outsider.Client.GetAsync(path, ct))
		{
			forbidden.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
		}

		using (var missing = await s.Coach.Client.GetAsync($"/api/v1/matches/{Guid.NewGuid()}/recording-lineage", ct))
		{
			missing.StatusCode.ShouldBe(HttpStatusCode.NotFound);
		}

		await new ClubHierarchyBuilder(s.Factory, s.Admin).ArchiveSeasonAsync(s.Hierarchy.SeasonId, ct);
		foreach (var session in new[] { viewer, s.Coach, s.Admin })
		{
			foreach (var read in new[] { path, one.VersionLocation, one.MappingLocation })
			{
				using var response = await session.Client.GetAsync(read, ct);
				response.StatusCode.ShouldBe(HttpStatusCode.OK);
			}
		}

		using var mutation = await PostAsync(s.Coach, $"/api/v1/matches/{match}/recording-versions/{one.Version}/timeline-mappings", Spans(9), ct);
		mutation.StatusCode.ShouldBe(HttpStatusCode.Conflict);
		(await mutation.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("code").GetString().ShouldBe("season-archived");
	}

	private static void AssertNoStorageDetails(string text)
	{
		foreach (var forbidden in new[] { "objectKey", "object_key", "uploadId", "multipart", "http://", "https://", "grant", "storageETag" })
		{
			text.ShouldNotContain(forbidden, Case.Insensitive);
		}
	}
}
