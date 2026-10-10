using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Shouldly;
using SocAlytics.Platform.Integration.Tests.Infrastructure;
using SocAlytics.Platform.Integration.Tests.Recordings.Support;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.Recordings;

public sealed class RecordingFinalizationHelperTests(PostgresContainerFixture postgres, RustFsContainerFixture rustFs)
{
	[Fact]
	public async Task HelperReturnsSetWhoseReadListsTheRecordingsAndMappingsInOrder()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var s = await FinalizationSetup.StartAsync(postgres, rustFs, ct);

		var set = await RecordingFinalizationHelper.FinalizeAsync(s.Coach, s.Hierarchy.MatchId, 2, ct);

		using var read = await s.Coach.SendAsync(HttpMethod.Get, $"/api/v1/matches/{s.Hierarchy.MatchId}/recording-set-versions/{set.RecordingSetVersionId}", null, ct);
		read.StatusCode.ShouldBe(HttpStatusCode.OK, await read.Content.ReadAsStringAsync(ct));
		var body = await read.Content.ReadFromJsonAsync<JsonElement>(ct);
		var members = body.GetProperty("members").EnumerateArray().ToList();
		members.Select(m => m.GetProperty("recordingVersionId").GetGuid()).ShouldBe(set.RecordingVersionIds);
		members.Select(m => m.GetProperty("timelineMappingId").GetGuid()).ShouldBe(set.TimelineMappingIds);
	}
}
