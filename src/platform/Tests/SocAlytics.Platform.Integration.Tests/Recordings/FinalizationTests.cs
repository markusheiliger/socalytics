using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;
using Npgsql;
using Shouldly;
using SocAlytics.Platform.Integration.Tests.IdentityAccess.Support;
using SocAlytics.Platform.Integration.Tests.Infrastructure;
using SocAlytics.Platform.Integration.Tests.Recordings.Support;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.Recordings;

public sealed class FinalizationTests(PostgresContainerFixture postgres, RustFsContainerFixture rustFs)
{
	private static Task<HttpResponseMessage> PostAsync(ApiSession session, string path, object body, CancellationToken ct)
	{
		var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
		request.Headers.Add("X-CSRF-Token", session.AntiforgeryToken);
		request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
		return session.Client.SendAsync(request, ct);
	}

	private static Task<HttpResponseMessage> FinalizeRawAsync(ApiSession session, Guid matchId, object body, CancellationToken ct) =>
		PostAsync(session, $"/api/v1/matches/{matchId}/recording-set-versions", body, ct);

	private static async Task<(Guid Version, Guid Mapping, TimeSpan Start, TimeSpan Complete)> UploadAsync(
		ApiSession session, Guid matchId, int seed, CancellationToken ct)
	{
		var recording = RecordingTestData.Generate(seed, 2048);
		var clock = Stopwatch.StartNew();
		using var started = await PostAsync(session, $"/api/v1/matches/{matchId}/upload-sessions", recording.Declaration(), ct);
		var startTime = clock.Elapsed;
		started.StatusCode.ShouldBe(HttpStatusCode.Created, await started.Content.ReadAsStringAsync(ct));
		var body = await started.Content.ReadFromJsonAsync<JsonElement>(ct);
		using var storage = new HttpClient();
		using (var put = await RecordingTestData.PutPartAsync(storage, body.GetProperty("grants").GetProperty("parts")[0], recording.Parts[0], ct))
		{
			put.IsSuccessStatusCode.ShouldBeTrue();
		}

		var id = body.GetProperty("session").GetProperty("id").GetString();
		var spans = new[] { new { mediaStartSeconds = 0, mediaEndSeconds = 10, matchStartSeconds = 0 } };
		clock.Restart();
		using var completed = await PostAsync(session, $"/api/v1/matches/{matchId}/upload-sessions/{id}/completion", new { timelineMapping = new { spans } }, ct);
		var completeTime = clock.Elapsed;
		completed.StatusCode.ShouldBe(HttpStatusCode.Created, await completed.Content.ReadAsStringAsync(ct));
		var result = await completed.Content.ReadFromJsonAsync<JsonElement>(ct);
		return (result.GetProperty("recordingVersion").GetProperty("id").GetGuid(), result.GetProperty("timelineMapping").GetProperty("id").GetGuid(), startTime, completeTime);
	}

	private static object Members(IEnumerable<(Guid Version, Guid Mapping)> members) =>
		new { members = members.Select(m => new { recordingVersionId = m.Version, timelineMappingId = m.Mapping }).ToArray() };

	[Fact]
	// US2 AS1-AS3, SC-005, FR-030
	public async Task ThreeRecordingsFinalizeInChosenOrderWithOneEventAndAudit()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var s = await FinalizationSetup.StartAsync(postgres, rustFs, ct);
		var up = new List<(Guid Version, Guid Mapping, TimeSpan Start, TimeSpan Complete)>();
		for (var i = 0; i < 3; i++)
		{
			up.Add(await UploadAsync(s.Coach, s.Hierarchy.MatchId, 10 + i, ct));
		}

		var order = new[] { up[2], up[0], up[1] }.Select(u => (u.Version, u.Mapping)).ToList();
		using var response = await FinalizeRawAsync(s.Coach, s.Hierarchy.MatchId, Members(order), ct);
		response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(ct));
		response.Headers.Location.ShouldNotBeNull();
		var created = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
		var setId = created.GetProperty("id").GetGuid();
		var createdMembers = created.GetProperty("members").EnumerateArray().ToList();
		createdMembers.Select(m => m.GetProperty("recordingVersionId").GetGuid()).ShouldBe(order.Select(o => o.Version));

		for (var i = 0; i < 2; i++)
		{
			using var read = await s.Coach.SendAsync(HttpMethod.Get, $"/api/v1/matches/{s.Hierarchy.MatchId}/recording-set-versions/{setId}", null, ct);
			read.StatusCode.ShouldBe(HttpStatusCode.OK);
			read.Headers.ETag!.Tag.ShouldBe($"\"{setId}\"");
			var json = await read.Content.ReadFromJsonAsync<JsonElement>(ct);
			json.GetProperty("members").GetRawText().ShouldBe(created.GetProperty("members").GetRawText());
		}

		(await s.ScalarAsync("SELECT count(*) FROM socalytics.recording_finalized_events", ct)).ShouldBe(1);
		string payloadText, eventType, contractVersion, traceId;
		await using (var connection = new NpgsqlConnection(s.Db.MigratorConnectionString))
		{
			await connection.OpenAsync(ct);
			await using var command = new NpgsqlCommand("SELECT payload::text, event_type, contract_version FROM socalytics.recording_finalized_events", connection);
			await using var reader = await command.ExecuteReaderAsync(ct);
			(await reader.ReadAsync(ct)).ShouldBeTrue();
			(payloadText, eventType, contractVersion) = (reader.GetString(0), reader.GetString(1), reader.GetString(2));
			await reader.CloseAsync();
			await using var trace = new NpgsqlCommand("SELECT correlation_id FROM socalytics.security_audit_event WHERE resource_id = @r", connection);
			trace.Parameters.AddWithValue("r", setId.ToString());
			traceId = (string)(await trace.ExecuteScalarAsync(ct))!;
		}

		eventType.ShouldBe("matches.recordings-finalized");
		contractVersion.ShouldBe("1.0.0");
		var payload = JsonNode.Parse(payloadText)!;
		var payloadElement = JsonDocument.Parse(payloadText).RootElement;
		var schemaPath = Path.Combine(AppContext.BaseDirectory, "contracts", "recordings", "recordings-finalized", "v1", "recordings-finalized.schema.json");
		var schema = JsonSchema.FromText(File.ReadAllText(schemaPath), new BuildOptions { SchemaRegistry = new SchemaRegistry { Fetch = (_, _) => null } });
		schema.Evaluate(payloadElement, new EvaluationOptions { OutputFormat = OutputFormat.List }).IsValid.ShouldBeTrue(payloadText);
		payload["recordingSetVersionId"]!.GetValue<string>().ShouldBe(setId.ToString());
		var eventMembers = payload["members"]!.AsArray();
		eventMembers.Select(m => m!["recordingVersionId"]!.GetValue<string>()).ShouldBe(createdMembers.Select(m => m.GetProperty("recordingVersionId").GetString()!));
		eventMembers.Select(m => m!["recordingContentDigest"]!.GetValue<string>()).ShouldBe(createdMembers.Select(m => m.GetProperty("recordingContentDigest").GetString()!));
		eventMembers.Select(m => m!["timelineMappingDigest"]!.GetValue<string>()).ShouldBe(createdMembers.Select(m => m.GetProperty("timelineMappingDigest").GetString()!));
		eventMembers[0]!["recordingContentDigest"]!.GetValue<string>().ShouldStartWith("sha-256-parts:");

		await RecordingAuditAssertions.AssertSingleAsync(
			s.Db, "recording.set.finalize", setId.ToString(), s.CoachId, s.Hierarchy.TeamId, s.Hierarchy.MatchId, "succeeded",
			traceId, new HashSet<string> { "matchId", "memberCount" }, ct, "recording-set-version");
	}

	[Fact]
	// US2 AS4
	public async Task InvalidSetsAreRejectedWithoutRows()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var s = await FinalizationSetup.StartAsync(postgres, rustFs, ct, new() { ["MaxMembers"] = "3" });
		var a = await UploadAsync(s.Coach, s.Hierarchy.MatchId, 21, ct);
		var b = await UploadAsync(s.Coach, s.Hierarchy.MatchId, 22, ct);
		var otherMatch = await new ClubHierarchyBuilder(s.Factory, s.Admin).CreateMatchAsync(s.Hierarchy.TeamId, ct);
		var foreign = await UploadAsync(s.Coach, otherMatch, 23, ct);

		async Task<JsonElement> RejectAsync(object body, string code)
		{
			using var response = await FinalizeRawAsync(s.Coach, s.Hierarchy.MatchId, body, ct);
			response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, await response.Content.ReadAsStringAsync(ct));
			var json = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
			json.GetProperty("code").GetString().ShouldBe(code);
			return json;
		}

		await RejectAsync(new { members = Array.Empty<object>() }, "recording-set-empty");
		await RejectAsync(Members(Enumerable.Repeat((a.Version, a.Mapping), 4)), "recording-set-too-large");
		var cross = await RejectAsync(Members([(a.Version, a.Mapping), (foreign.Version, foreign.Mapping)]), "validation-failed");
		cross.GetProperty("errors")[0].GetProperty("field").GetString().ShouldBe("members[1]");
		var mismatch = await RejectAsync(Members([(a.Version, b.Mapping)]), "validation-failed");
		mismatch.GetProperty("errors")[0].GetProperty("field").GetString().ShouldBe("members[0]");
		var dup = await RejectAsync(Members([(a.Version, a.Mapping), (a.Version, a.Mapping)]), "validation-failed");
		dup.GetProperty("errors")[0].GetProperty("field").GetString().ShouldBe("members[1]");

		foreach (var table in new[] { "recording_set_versions", "recording_set_members", "recording_finalized_events" })
		{
			(await s.ScalarAsync($"SELECT count(*) FROM socalytics.{table}", ct)).ShouldBe(0);
		}

		(await s.ScalarAsync("SELECT count(*) FROM socalytics.recording_retry_outcomes WHERE operation = 'finalize-recording-set'", ct)).ShouldBe(0);
	}

	[Fact]
	// SC-002
	public async Task PlatformOperationsStayWithinTwoSecondsAtP95()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var s = await FinalizationSetup.StartAsync(postgres, rustFs, ct);
		var seed = 100;
		async Task<TimeSpan[]> FlowAsync()
		{
			var ups = new List<(Guid Version, Guid Mapping, TimeSpan Start, TimeSpan Complete)>();
			for (var i = 0; i < 3; i++)
			{
				ups.Add(await UploadAsync(s.Coach, s.Hierarchy.MatchId, seed++, ct));
			}

			var clock = Stopwatch.StartNew();
			using var response = await FinalizeRawAsync(s.Coach, s.Hierarchy.MatchId, Members(ups.Select(u => (u.Version, u.Mapping))), ct);
			var finalize = clock.Elapsed;
			response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(ct));
			return [ups.Max(u => u.Start), ups.Max(u => u.Complete), finalize];
		}

		await FlowAsync();
		List<TimeSpan> starts = [], completes = [], finalizes = [];
		for (var i = 0; i < 20; i++)
		{
			var t = await FlowAsync();
			starts.Add(t[0]);
			completes.Add(t[1]);
			finalizes.Add(t[2]);
		}

		foreach (var samples in new[] { starts, completes, finalizes })
		{
			samples.Order().ElementAt(18).ShouldBeLessThanOrEqualTo(TimeSpan.FromSeconds(2));
		}
	}
}
