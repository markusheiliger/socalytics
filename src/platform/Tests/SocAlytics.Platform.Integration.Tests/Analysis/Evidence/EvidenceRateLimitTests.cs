using System.Net;
using System.Text.Json;
using Shouldly;
using SocAlytics.Platform.Integration.Tests.Analysis.Evidence.Support;
using SocAlytics.Platform.Integration.Tests.Infrastructure;
using SocAlytics.Platform.Integration.Tests.Recordings;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.Analysis.Evidence;

// FR-015: resolutions are limited per member; a refused request issues no link and records no demand.
public sealed class EvidenceRateLimitTests(PostgresContainerFixture postgres, RustFsContainerFixture rustFs)
{
	private const string Granted = "SELECT count(*) FROM socalytics.security_audit_event WHERE event_type = 'analysis.evidence-media.granted'";

	private const string Rows = "SELECT count(*) FROM socalytics.materialized_segments";

	private const string Demanded = "SELECT count(*) FROM socalytics.materialized_segments WHERE demanded";

	[Fact]
	public async Task RequestOverTheLimitIsRefusedWithoutLinkOrDemandAndTheLimitIsPerMember()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var scenario = await EvidenceScenario.StartAsync(
			postgres, rustFs, ct, extraSettings: new() { ["Analysis:Evidence:ResolutionsPerMinute"] = "1" });

		using (var first = await ResolveAsync(scenario, scenario.Admin, EvidenceScenario.InsideFactOrdinal, ct))
		{
			first.StatusCode.ShouldBe(HttpStatusCode.OK);
		}

		var rows = await scenario.ScalarAsync(Rows, ct);
		var demanded = await scenario.ScalarAsync(Demanded, ct);
		var states = await scenario.ScalarAsync("SELECT coalesce(sum(version), 0) FROM socalytics.materialized_segments", ct);

		using var limited = await ResolveAsync(scenario, scenario.Admin, EvidenceScenario.BoundaryFactOrdinal, ct);
		var text = await limited.Content.ReadAsStringAsync(ct);
		limited.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests, text);
		JsonDocument.Parse(text).RootElement.GetProperty("code").GetString().ShouldBe("evidence-rate-limited");
		limited.Headers.RetryAfter.ShouldNotBeNull().Delta.ShouldNotBeNull().TotalSeconds.ShouldBeGreaterThanOrEqualTo(1);
		limited.Headers.CacheControl.ShouldNotBeNull().NoStore.ShouldBeTrue();
		text.ShouldNotContain("X-Amz-Signature");
		text.ShouldNotContain("\"url\"");

		(await scenario.ScalarAsync(Granted, ct)).ShouldBe(0);
		(await scenario.ScalarAsync(Rows, ct)).ShouldBe(rows);
		(await scenario.ScalarAsync(Demanded, ct)).ShouldBe(demanded);
		(await scenario.ScalarAsync("SELECT coalesce(sum(version), 0) FROM socalytics.materialized_segments", ct)).ShouldBe(states);

		// Another member has an own allowance.
		using var coach = await ResolveAsync(scenario, scenario.Coach, EvidenceScenario.BoundaryFactOrdinal, ct);
		coach.StatusCode.ShouldBe(HttpStatusCode.OK, await coach.Content.ReadAsStringAsync(ct));
	}

	private static Task<HttpResponseMessage> ResolveAsync(
		EvidenceScenario scenario, IdentityAccess.Support.ApiSession session, int ordinal, CancellationToken ct) =>
		session.SendAsync(
			HttpMethod.Post,
			scenario.EvidencePath,
			new { fact = new { acceptedResultId = scenario.ResultId, ordinal } },
			ct);
}
