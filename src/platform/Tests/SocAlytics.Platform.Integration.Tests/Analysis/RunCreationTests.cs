using System.Net;
using System.Text.Json;
using Shouldly;
using SocAlytics.Platform.Integration.Tests.Infrastructure;
using SocAlytics.Platform.Integration.Tests.Recordings;
using SocAlytics.Platform.Integration.Tests.Recordings.Support;
using Npgsql;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.Analysis;

public sealed class RunCreationTests(PostgresContainerFixture postgres, RustFsContainerFixture rustFs)
{
	[Fact]
	public async Task FinalizationEnqueuesOneOutboxRowEqualToTheEventRow()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var s = await FinalizationSetup.StartAsync(postgres, rustFs, ct);

		var set = await RecordingFinalizationHelper.FinalizeAsync(s.Coach, s.Hierarchy.MatchId, 1, ct);

		(await s.ScalarAsync("SELECT count(*) FROM socalytics.outbox_messages", ct)).ShouldBe(1);
		await using var connection = new NpgsqlConnection(s.Db.MigratorConnectionString);
		await connection.OpenAsync(ct);
		await using var command = new NpgsqlCommand(
			"""
			SELECT o.id, o.causation_id, o.state, o.message_type, o.subject, o.contract_version, o.payload::text, e.event_id, e.payload::text
			FROM socalytics.outbox_messages o
			JOIN socalytics.recording_finalized_events e ON e.event_id = o.id
			WHERE e.recording_set_version_id = @set
			""", connection);
		command.Parameters.AddWithValue("set", set.RecordingSetVersionId);
		await using var reader = await command.ExecuteReaderAsync(ct);
		(await reader.ReadAsync(ct)).ShouldBeTrue();
		reader.GetGuid(0).ShouldBe(reader.GetGuid(7));
		reader.GetGuid(1).ShouldBe(reader.GetGuid(7));
		reader.GetString(2).ShouldBe("pending");
		reader.GetString(3).ShouldBe("matches.recordings-finalized");
		reader.GetString(4).ShouldBe("matches.recordings-finalized");
		reader.GetString(5).ShouldBe("1.0.0");
		JsonDocument.Parse(reader.GetString(6)).RootElement.GetRawText()
			.ShouldBe(JsonDocument.Parse(reader.GetString(8)).RootElement.GetRawText());
	}

	[Fact]
	public async Task ReplayedFinalizationLeavesOneOutboxRow()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var s = await FinalizationSetup.StartAsync(postgres, rustFs, ct);
		var recording = await RecordingFinalizationHelper.FinalizeAsync(s.Coach, s.Hierarchy.MatchId, 1, ct);
		var members = new[] { new { recordingVersionId = recording.RecordingVersionIds[0], timelineMappingId = recording.TimelineMappingIds[0] } };

		for (var i = 0; i < 2; i++)
		{
			using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/matches/{s.Hierarchy.MatchId}/recording-set-versions")
			{
				Content = System.Net.Http.Json.JsonContent.Create(new { members }),
			};
			request.Headers.Add("X-CSRF-Token", s.Coach.AntiforgeryToken);
			request.Headers.Add("Idempotency-Key", "replay-key");
			using var response = await s.Coach.Client.SendAsync(request, ct);
			response.StatusCode.ShouldBeOneOf(HttpStatusCode.Created, HttpStatusCode.OK);
		}

		(await s.ScalarAsync("SELECT count(*) FROM socalytics.outbox_messages", ct)).ShouldBe(2);
		(await s.ScalarAsync("SELECT count(*) FROM socalytics.recording_finalized_events", ct)).ShouldBe(2);
	}
}
