using System.Net;
using System.Text.Json;
using Dapper;
using Npgsql;
using Shouldly;
using SocAlytics.Platform.Integration.Tests.Analysis.Evidence.Support;
using SocAlytics.Platform.Integration.Tests.Infrastructure;
using SocAlytics.Platform.Integration.Tests.Recordings;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.Analysis.Evidence;

public sealed class EvidenceAuditAndLoggingTests(PostgresContainerFixture postgres, RustFsContainerFixture rustFs)
{
	private const string GrantedEventType = "analysis.evidence-media.granted";

	[Fact]
	public async Task EveryResolutionWithLinksRecordsOneGrantedEventWithItsFields()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var scenario = await EvidenceScenario.StartAsync(postgres, rustFs, ct);
		await scenario.MaterializeAllAsync(ct);

		var first = await ResolveBoundaryFactAsync(scenario, ct);
		(await GrantedEventsAsync(scenario, ct)).Count.ShouldBe(1);

		var second = await ResolveBoundaryFactAsync(scenario, ct);
		var events = await GrantedEventsAsync(scenario, ct);
		events.Count.ShouldBe(2);

		var row = events[0];
		row.ActorKind.ShouldBe("member");
		row.Outcome.ShouldBe("succeeded");
		row.ResourceType.ShouldBe("analysis-run");
		row.ResourceId.ShouldBe(scenario.RunId.ToString());
		row.TeamId.ShouldBe(scenario.TeamId);

		using var details = JsonDocument.Parse(row.Details);
		var root = details.RootElement;
		root.GetProperty("runId").GetString().ShouldBe(scenario.RunId.ToString());
		root.GetProperty("matchId").GetString().ShouldBe(scenario.MatchId.ToString());
		root.GetProperty("kind").GetString().ShouldBe("fact");
		root.GetProperty("acceptedResultId").GetString().ShouldBe(scenario.ResultId.ToString());
		root.GetProperty("ordinal").GetString().ShouldBe(EvidenceScenario.BoundaryFactOrdinal.ToString());

		var requested = first.GetProperty("requested");
		root.GetProperty("fromMs").GetString().ShouldBe(requested.GetProperty("fromMs").GetInt64().ToString());
		root.GetProperty("toMs").GetString().ShouldBe(requested.GetProperty("toMs").GetInt64().ToString());
		root.GetProperty("shortened").GetString().ShouldBe(first.GetProperty("shortened").GetBoolean() ? "true" : "false");

		var media = MediaOf(first);
		media.Count.ShouldBe(2);
		root.GetProperty("grantedPartCount").GetString().ShouldBe(media.Count.ToString());
		root.GetProperty("mediaDigests").GetString()!.Split(',').ToHashSet()
			.ShouldBe(media.Select(m => m.GetProperty("mediaDigest").GetString()!).ToHashSet());
		var latest = media.Max(m => DateTimeOffset.Parse(m.GetProperty("expiresAt").GetString()!));
		var recorded = DateTimeOffset.Parse(root.GetProperty("grantExpiresAt").GetString()!);
		(recorded - latest).Duration().ShouldBeLessThan(TimeSpan.FromSeconds(1));

		MediaOf(second).Count.ShouldBe(2);
	}

	[Fact]
	public async Task ResolutionWithoutLinksRecordsNoGrantedEvent()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var scenario = await EvidenceScenario.StartAsync(postgres, rustFs, ct);

		// No segment is materialized yet, so the resolution issues no link.
		var body = await ResolveBoundaryFactAsync(scenario, ct);
		MediaOf(body).ShouldBeEmpty();

		(await GrantedEventsAsync(scenario, ct)).ShouldBeEmpty();
	}

	[Fact]
	public async Task LinksSignaturesAndObjectKeysNeverReachAuditDetailsLogsOrStoredRows()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var scenario = await EvidenceScenario.StartAsync(postgres, rustFs, ct);
		await scenario.MaterializeAllAsync(ct);

		var urls = new List<string>();
		urls.AddRange(MediaOf(await ResolveBoundaryFactAsync(scenario, ct)).Select(m => m.GetProperty("url").GetString()!));
		urls.AddRange(MediaOf(await ResolveAsync(scenario, new
		{
			fact = new { acceptedResultId = scenario.ResultId, ordinal = EvidenceScenario.InsideFactOrdinal, contextBeforeMs = 500, contextAfterMs = 500 },
		}, ct)).Select(m => m.GetProperty("url").GetString()!));
		urls.Count.ShouldBeGreaterThan(0);

		var objectKeys = await QueryAsync(scenario,
			"SELECT media_object_key FROM socalytics.materialized_segments WHERE media_object_key IS NOT NULL " +
			"UNION SELECT manifest_object_key FROM socalytics.materialized_segments WHERE manifest_object_key IS NOT NULL", ct);
		objectKeys.Count.ShouldBeGreaterThan(0);

		var auditDetails = await QueryAsync(scenario, "SELECT details::text FROM socalytics.security_audit_event", ct);
		auditDetails.Count.ShouldBeGreaterThan(0);
		var auditRows = await QueryAsync(scenario, "SELECT to_jsonb(e)::text FROM socalytics.security_audit_event e", ct);
		var segmentRows = await QueryAsync(scenario, "SELECT to_jsonb(s)::text FROM socalytics.materialized_segments s", ct);
		var outboxRows = await QueryAsync(scenario, "SELECT to_jsonb(o)::text FROM socalytics.outbox_messages o", ct);

		var logs = scenario.Logs.Entries
			.SelectMany(e => new[] { e.Message, e.Exception ?? string.Empty }
				.Concat(e.State.Select(p => $"{p.Key}={p.Value}")))
			.ToList();
		logs.Count.ShouldBeGreaterThan(0);

		foreach (var texts in new[] { auditRows, segmentRows, outboxRows, logs })
		{
			AssertNoLinks(texts, urls);
		}

		foreach (var texts in new[] { auditDetails, logs })
		{
			foreach (var key in objectKeys)
			{
				texts.ShouldNotContain(t => t.Contains(key, StringComparison.Ordinal));
			}
		}
	}

	private static void AssertNoLinks(IReadOnlyList<string> texts, IReadOnlyList<string> urls)
	{
		texts.ShouldNotContain(t => t.Contains("X-Amz-Signature", StringComparison.OrdinalIgnoreCase));
		foreach (var url in urls)
		{
			texts.ShouldNotContain(t => t.Contains(url, StringComparison.Ordinal));
			var signature = System.Web.HttpUtility.ParseQueryString(new Uri(url).Query)["X-Amz-Signature"];
			if (!string.IsNullOrEmpty(signature))
			{
				texts.ShouldNotContain(t => t.Contains(signature, StringComparison.Ordinal));
			}
		}
	}

	private static Task<JsonElement> ResolveBoundaryFactAsync(EvidenceScenario scenario, CancellationToken ct) =>
		ResolveAsync(scenario, new
		{
			fact = new { acceptedResultId = scenario.ResultId, ordinal = EvidenceScenario.BoundaryFactOrdinal, contextBeforeMs = 0, contextAfterMs = 0 },
		}, ct);

	private static async Task<JsonElement> ResolveAsync(EvidenceScenario scenario, object body, CancellationToken ct)
	{
		using var response = await scenario.Coach.SendAsync(HttpMethod.Post, scenario.EvidencePath, body, ct);
		var text = await response.Content.ReadAsStringAsync(ct);
		response.StatusCode.ShouldBe(HttpStatusCode.OK, text);
		return JsonDocument.Parse(text).RootElement.Clone();
	}

	private static List<JsonElement> MediaOf(JsonElement body) =>
		[.. body.GetProperty("parts").EnumerateArray().Where(p => p.TryGetProperty("media", out _)).Select(p => p.GetProperty("media"))];

	private static async Task<List<string>> QueryAsync(EvidenceScenario scenario, string sql, CancellationToken ct)
	{
		await using var connection = new NpgsqlConnection(scenario.Setup.Db.MigratorConnectionString);
		await connection.OpenAsync(ct);
		return [.. await connection.QueryAsync<string>(new CommandDefinition(sql, cancellationToken: ct))];
	}

	private static async Task<List<AuditRow>> GrantedEventsAsync(EvidenceScenario scenario, CancellationToken ct)
	{
		await using var connection = new NpgsqlConnection(scenario.Setup.Db.MigratorConnectionString);
		await connection.OpenAsync(ct);
		return [.. await connection.QueryAsync<AuditRow>(new CommandDefinition(
			"""
			SELECT actor_kind AS ActorKind, outcome AS Outcome, resource_type AS ResourceType, resource_id AS ResourceId,
				team_id AS TeamId, details::text AS Details
			FROM socalytics.security_audit_event WHERE event_type = @type ORDER BY occurred_at
			""",
			new { type = GrantedEventType }, cancellationToken: ct))];
	}

	private sealed record AuditRow(string ActorKind, string Outcome, string ResourceType, string? ResourceId, Guid? TeamId, string Details);
}
