using System.Text.Json;
using Npgsql;
using Shouldly;
using SocAlytics.Platform.Integration.Tests.Infrastructure;

namespace SocAlytics.Platform.Integration.Tests.Recordings;

internal static class RecordingAuditAssertions
{
	public static async Task AssertSingleAsync(
		IsolatedDatabase db,
		string eventType,
		string resourceId,
		Guid actorAccountId,
		Guid teamId,
		Guid matchId,
		string outcome,
		string traceId,
		IReadOnlySet<string> allowedDetailKeys,
		CancellationToken cancellationToken)
	{
		await using var connection = new NpgsqlConnection(db.MigratorConnectionString);
		await connection.OpenAsync(cancellationToken);
		await using var command = new NpgsqlCommand(
			"SELECT actor_kind, actor_account_id, resource_type, team_id, details::text, outcome, correlation_id " +
			"FROM socalytics.security_audit_event WHERE event_type = @t AND resource_id = @r",
			connection);
		command.Parameters.AddWithValue("t", eventType);
		command.Parameters.AddWithValue("r", resourceId);
		await using var reader = await command.ExecuteReaderAsync(cancellationToken);
		(await reader.ReadAsync(cancellationToken)).ShouldBeTrue();
		reader.GetString(0).ShouldBe("member");
		reader.GetGuid(1).ShouldBe(actorAccountId);
		reader.GetString(2).ShouldBe("upload-session");
		reader.GetGuid(3).ShouldBe(teamId);
		var details = JsonDocument.Parse(reader.GetString(4)).RootElement;
		details.GetProperty("matchId").GetString().ShouldBe(matchId.ToString());
		foreach (var property in details.EnumerateObject())
		{
			allowedDetailKeys.ShouldContain(property.Name);
		}

		reader.GetString(5).ShouldBe(outcome);
		reader.GetString(6).ShouldBe(traceId);
		(await reader.ReadAsync(cancellationToken)).ShouldBeFalse();
	}

	public static async Task AssertNoGrantUrlAsync(IsolatedDatabase db, CancellationToken cancellationToken)
	{
		await using var connection = new NpgsqlConnection(db.MigratorConnectionString);
		await connection.OpenAsync(cancellationToken);
		await using var command = new NpgsqlCommand(
			"SELECT count(*) FROM socalytics.security_audit_event WHERE details::text ~* '(x-amz|https?://|signature)'", connection);
		((long)(await command.ExecuteScalarAsync(cancellationToken))!).ShouldBe(0);
	}
}
