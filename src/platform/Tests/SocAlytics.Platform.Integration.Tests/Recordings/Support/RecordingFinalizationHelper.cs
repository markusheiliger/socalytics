using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Shouldly;
using SocAlytics.Platform.Integration.Tests.IdentityAccess.Support;

namespace SocAlytics.Platform.Integration.Tests.Recordings.Support;

internal sealed record FinalizedRecordingSet(
	Guid RecordingSetVersionId, IReadOnlyList<Guid> RecordingVersionIds, IReadOnlyList<Guid> TimelineMappingIds);

// Uploads, completes, and finalizes small recordings through the API; consumed by later features.
internal static class RecordingFinalizationHelper
{
	public static async Task<FinalizedRecordingSet> FinalizeAsync(ApiSession session, Guid matchId, int recordingCount, CancellationToken cancellationToken)
	{
		var versions = new List<Guid>();
		var mappings = new List<Guid>();
		using var storage = new HttpClient();
		for (var i = 0; i < recordingCount; i++)
		{
			var recording = RecordingTestData.Generate(Environment.TickCount + i + 1, 1024 + i);
			var started = await SendAsync(session, $"/api/v1/matches/{matchId}/upload-sessions", recording.Declaration(), HttpStatusCode.Created, cancellationToken);
			var id = started.GetProperty("session").GetProperty("id").GetString()!;
			var parts = started.GetProperty("grants").GetProperty("parts").EnumerateArray().ToList();
			for (var p = 0; p < parts.Count; p++)
			{
				using var put = await RecordingTestData.PutPartAsync(storage, parts[p], recording.Parts[p], cancellationToken);
				put.IsSuccessStatusCode.ShouldBeTrue(await put.Content.ReadAsStringAsync(cancellationToken));
			}

			var spans = new[] { new { mediaStartSeconds = 0, mediaEndSeconds = 10, matchStartSeconds = 0 } };
			var completed = await SendAsync(
				session, $"/api/v1/matches/{matchId}/upload-sessions/{id}/completion", new { timelineMapping = new { spans } }, HttpStatusCode.Created, cancellationToken);
			versions.Add(completed.GetProperty("recordingVersion").GetProperty("id").GetGuid());
			mappings.Add(completed.GetProperty("timelineMapping").GetProperty("id").GetGuid());
		}

		var members = versions.Select((v, i) => new { recordingVersionId = v, timelineMappingId = mappings[i] }).ToArray();
		var set = await SendAsync(session, $"/api/v1/matches/{matchId}/recording-set-versions", new { members }, HttpStatusCode.Created, cancellationToken);
		return new FinalizedRecordingSet(set.GetProperty("id").GetGuid(), versions, mappings);
	}

	private static async Task<JsonElement> SendAsync(ApiSession session, string path, object body, HttpStatusCode expected, CancellationToken cancellationToken)
	{
		using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
		request.Headers.Add("X-CSRF-Token", session.AntiforgeryToken);
		request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
		using var response = await session.Client.SendAsync(request, cancellationToken);
		var text = await response.Content.ReadAsStringAsync(cancellationToken);
		response.StatusCode.ShouldBe(expected, text);
		return JsonDocument.Parse(text).RootElement.Clone();
	}
}
