using System.Net;
using System.Text.Json;
using Shouldly;
using SocAlytics.Platform.Integration.Tests.AgentOrchestration.Catalog.Support;
using SocAlytics.Platform.Integration.Tests.AgentOrchestration.Support;
using SocAlytics.Platform.Integration.Tests.IdentityAccess.Support;
using SocAlytics.Platform.Integration.Tests.Infrastructure;
using Npgsql;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.AgentOrchestration.Catalog;

// FR-015, FR-017, SC-002, SC-005: approval and retirement of agent versions are conditional, single-winner, audited, and never change the document.
public sealed class LifecycleTests(PostgresContainerFixture postgres)
{
	private const string AgentId = "specialist-tactics";
	private const string Version = "1.0.0";

	private sealed record Reply(HttpStatusCode Status, JsonElement Body, string? ETag);

	private static async Task<Reply> ReadAsync(HttpResponseMessage response, CancellationToken ct)
	{
		using (response)
		{
			var text = await response.Content.ReadAsStringAsync(ct);
			var body = text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
			return new Reply(response.StatusCode, body, response.Headers.ETag?.Tag);
		}
	}

	private static void ShouldBeProblem(Reply reply, HttpStatusCode status, string code)
	{
		reply.Status.ShouldBe(status, reply.Body.ToString());
		reply.Body.GetProperty("code").GetString().ShouldBe(code);
	}

	// Registers the specialist's artifacts and its draft version as the fixture's Club Admin and returns the draft's ETag.
	private static async Task<string> RegisterDraftAsync(AgentCatalogApi api, CancellationToken ct)
	{
		var artifacts = CatalogFixtures.SpecialistTacticsArtifacts();
		foreach (var artifact in artifacts.All)
		{
			var stored = await ReadAsync(await api.RegisterArtifactAsync(artifact, ct), ct);
			stored.Status.ShouldBe(HttpStatusCode.Created, $"{artifact.Kind}: {stored.Body}");
		}

		var registered = await ReadAsync(await api.RegisterAgentVersionAsync(Version, CatalogFixtures.AgentVersion(artifacts), ct), ct);
		registered.Status.ShouldBe(HttpStatusCode.Created, registered.Body.ToString());
		registered.ETag.ShouldNotBeNull();
		return registered.ETag;
	}

	private static async Task<Reply> GetAsync(AgentCatalogApi api, CancellationToken ct) =>
		await ReadAsync(await api.GetAgentVersionAsync(AgentId, Version, null, ct), ct);

	private static async Task<ApiSession> SecondAdminAsync(AgentOrchestrationFixture f, CancellationToken ct)
	{
		await TestMembers.SeedAsync(f.Db, "admin-2", clubRoles: ["club-admin"], cancellationToken: ct);
		return await ApiSession.SignInAsync(f.Factory, "admin-2", TestMembers.DefaultPassword, ct);
	}

	private static async Task SetEnabledVersionAsync(AgentOrchestrationFixture f, string? version, CancellationToken ct)
	{
		await using var connection = new NpgsqlConnection(f.Db.MigratorConnectionString);
		await connection.OpenAsync(ct);
		await using var command = new NpgsqlCommand(
			"UPDATE socalytics.agent_enablements SET agent_version = @version WHERE agent_id = @agent", connection);
		command.Parameters.AddWithValue("version", (object?)version ?? DBNull.Value);
		command.Parameters.AddWithValue("agent", AgentId);
		(await command.ExecuteNonQueryAsync(ct)).ShouldBe(1);
	}

	private static Task<long> AuditAsync(AgentOrchestrationFixture f, string eventType, string outcome, CancellationToken ct) =>
		f.ScalarAsync(
			$"SELECT count(*) FROM socalytics.security_audit_event WHERE event_type = '{eventType}' AND outcome = '{outcome}'", ct);

	[Fact]
	public async Task ApprovalByTheCreatorMarksSelfApproval()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var f = await AgentOrchestrationFixture.StartAsync(postgres, ct, CatalogSeed.Off);
		var api = new AgentCatalogApi(f.Admin);
		var etag = await RegisterDraftAsync(api, ct);
		var draft = await GetAsync(api, ct);

		var approved = await ReadAsync(await api.ApproveAgentVersionAsync(AgentId, Version, etag, null, ct), ct);

		approved.Status.ShouldBe(HttpStatusCode.OK, approved.Body.ToString());
		approved.Body.GetProperty("state").GetString().ShouldBe("approved");
		approved.Body.GetProperty("selfApproval").GetBoolean().ShouldBeTrue();
		approved.Body.GetProperty("approvedBy").GetString().ShouldBe(draft.Body.GetProperty("registeredBy").GetString());
		approved.ETag.ShouldNotBe(etag);
		(await AuditAsync(f, "agent.catalog.version.approved", "succeeded", ct)).ShouldBe(1);
	}

	[Fact]
	public async Task ApprovalByAnotherClubAdminIsNotSelfApproval()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var f = await AgentOrchestrationFixture.StartAsync(postgres, ct, CatalogSeed.Off);
		using var other = await SecondAdminAsync(f, ct);
		var etag = await RegisterDraftAsync(new AgentCatalogApi(f.Admin), ct);

		var approved = await ReadAsync(await new AgentCatalogApi(other).ApproveAgentVersionAsync(AgentId, Version, etag, null, ct), ct);

		approved.Status.ShouldBe(HttpStatusCode.OK, approved.Body.ToString());
		approved.Body.GetProperty("selfApproval").GetBoolean().ShouldBeFalse();
	}

	[Fact]
	public async Task ApprovingTwiceAndApprovingARetiredVersionAreRefused()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var f = await AgentOrchestrationFixture.StartAsync(postgres, ct, CatalogSeed.Off);
		var api = new AgentCatalogApi(f.Admin);
		var etag = await RegisterDraftAsync(api, ct);
		var approved = await ReadAsync(await api.ApproveAgentVersionAsync(AgentId, Version, etag, null, ct), ct);

		var again = await ReadAsync(await api.ApproveAgentVersionAsync(AgentId, Version, approved.ETag, null, ct), ct);
		ShouldBeProblem(again, HttpStatusCode.Conflict, "agent-version-not-draft");

		var retired = await ReadAsync(await api.RetireAgentVersionAsync(AgentId, Version, approved.ETag, ct), ct);
		retired.Status.ShouldBe(HttpStatusCode.OK, retired.Body.ToString());

		var afterRetirement = await ReadAsync(await api.ApproveAgentVersionAsync(AgentId, Version, retired.ETag, null, ct), ct);
		ShouldBeProblem(afterRetirement, HttpStatusCode.Conflict, "agent-version-not-draft");
		(await AuditAsync(f, "agent.catalog.version.approved", "succeeded", ct)).ShouldBe(1);
	}

	[Fact]
	public async Task StaleAndMissingIfMatchAreRefused()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var f = await AgentOrchestrationFixture.StartAsync(postgres, ct, CatalogSeed.Off);
		var api = new AgentCatalogApi(f.Admin);
		var etag = await RegisterDraftAsync(api, ct);

		(await ReadAsync(await api.ApproveAgentVersionAsync(AgentId, Version, null, null, ct), ct)).Status
			.ShouldBe(HttpStatusCode.PreconditionRequired);
		(await ReadAsync(await api.ApproveAgentVersionAsync(AgentId, Version, "\"999\"", null, ct), ct)).Status
			.ShouldBe(HttpStatusCode.PreconditionFailed);
		(await GetAsync(api, ct)).Body.GetProperty("state").GetString().ShouldBe("draft");

		var approved = await ReadAsync(await api.ApproveAgentVersionAsync(AgentId, Version, etag, null, ct), ct);
		approved.Status.ShouldBe(HttpStatusCode.OK, approved.Body.ToString());

		(await ReadAsync(await api.RetireAgentVersionAsync(AgentId, Version, null, ct), ct)).Status
			.ShouldBe(HttpStatusCode.PreconditionRequired);
		(await ReadAsync(await api.RetireAgentVersionAsync(AgentId, Version, etag, ct), ct)).Status
			.ShouldBe(HttpStatusCode.PreconditionFailed);
		(await GetAsync(api, ct)).Body.GetProperty("state").GetString().ShouldBe("approved");
	}

	[Fact]
	public async Task ConcurrentApprovalsLeaveOneWinner()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var f = await AgentOrchestrationFixture.StartAsync(postgres, ct, CatalogSeed.Off);
		using var other = await SecondAdminAsync(f, ct);
		var first = new AgentCatalogApi(f.Admin);
		var second = new AgentCatalogApi(other);
		var etag = await RegisterDraftAsync(first, ct);

		var replies = await Task.WhenAll(
			ReadAsync(await first.ApproveAgentVersionAsync(AgentId, Version, etag, null, ct), ct),
			ReadAsync(await second.ApproveAgentVersionAsync(AgentId, Version, etag, null, ct), ct));

		replies.Count(r => r.Status == HttpStatusCode.OK).ShouldBe(1);
		replies.Single(r => r.Status != HttpStatusCode.OK).Status.ShouldBeOneOf(HttpStatusCode.Conflict, HttpStatusCode.PreconditionFailed);
		(await AuditAsync(f, "agent.catalog.version.approved", "succeeded", ct)).ShouldBe(1);
		(await GetAsync(first, ct)).Body.GetProperty("state").GetString().ShouldBe("approved");
	}

	[Fact]
	public async Task ApprovedAndRetiredDocumentsAndDigestsNeverChange()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var f = await AgentOrchestrationFixture.StartAsync(postgres, ct, CatalogSeed.Off);
		var api = new AgentCatalogApi(f.Admin);
		var etag = await RegisterDraftAsync(api, ct);
		var draft = await GetAsync(api, ct);
		var document = draft.Body.GetProperty("document").GetRawText();
		var digest = draft.Body.GetProperty("digest").GetString();

		var approved = await ReadAsync(await api.ApproveAgentVersionAsync(AgentId, Version, etag, null, ct), ct);
		var afterApproval = await GetAsync(api, ct);
		var retired = await ReadAsync(await api.RetireAgentVersionAsync(AgentId, Version, approved.ETag, ct), ct);
		var afterRetirement = await GetAsync(api, ct);

		foreach (var reply in new[] { approved, afterApproval, retired, afterRetirement })
		{
			reply.Body.GetProperty("document").GetRawText().ShouldBe(document);
			reply.Body.GetProperty("digest").GetString().ShouldBe(digest);
		}

		afterRetirement.Body.GetProperty("state").GetString().ShouldBe("retired");
	}

	[Fact]
	public async Task RetiringAnApprovedVersionSucceedsAndIsAudited()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var f = await AgentOrchestrationFixture.StartAsync(postgres, ct, CatalogSeed.Off);
		var api = new AgentCatalogApi(f.Admin);
		var etag = await RegisterDraftAsync(api, ct);

		var draftRetirement = await ReadAsync(await api.RetireAgentVersionAsync(AgentId, Version, etag, ct), ct);
		ShouldBeProblem(draftRetirement, HttpStatusCode.Conflict, "agent-version-not-approved");

		var approved = await ReadAsync(await api.ApproveAgentVersionAsync(AgentId, Version, etag, null, ct), ct);
		var retired = await ReadAsync(await api.RetireAgentVersionAsync(AgentId, Version, approved.ETag, ct), ct);

		retired.Status.ShouldBe(HttpStatusCode.OK, retired.Body.ToString());
		retired.Body.GetProperty("state").GetString().ShouldBe("retired");
		retired.Body.GetProperty("retiredBy").GetString().ShouldNotBeNullOrEmpty();
		retired.Body.GetProperty("retiredAt").ValueKind.ShouldNotBe(JsonValueKind.Null);
		(await AuditAsync(f, "agent.catalog.version.retired", "succeeded", ct)).ShouldBe(1);

		var again = await ReadAsync(await api.RetireAgentVersionAsync(AgentId, Version, retired.ETag, ct), ct);
		ShouldBeProblem(again, HttpStatusCode.Conflict, "agent-version-not-approved");
		(await AuditAsync(f, "agent.catalog.version.retired", "succeeded", ct)).ShouldBe(1);
	}

	[Fact]
	public async Task RetiringTheEnabledVersionIsRefused()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var f = await AgentOrchestrationFixture.StartAsync(postgres, ct, CatalogSeed.Off);
		var api = new AgentCatalogApi(f.Admin);
		var etag = await RegisterDraftAsync(api, ct);
		var approved = await ReadAsync(await api.ApproveAgentVersionAsync(AgentId, Version, etag, null, ct), ct);
		await SetEnabledVersionAsync(f, Version, ct);

		var refused = await ReadAsync(await api.RetireAgentVersionAsync(AgentId, Version, approved.ETag, ct), ct);

		ShouldBeProblem(refused, HttpStatusCode.Conflict, "agent-version-enabled");
		var stored = await GetAsync(api, ct);
		stored.Body.GetProperty("state").GetString().ShouldBe("approved");
		stored.Body.GetProperty("enabled").GetBoolean().ShouldBeTrue();
		(await AuditAsync(f, "agent.catalog.version.retired", "succeeded", ct)).ShouldBe(0);

		await SetEnabledVersionAsync(f, null, ct);
		var retired = await ReadAsync(await api.RetireAgentVersionAsync(AgentId, Version, stored.ETag, ct), ct);
		retired.Status.ShouldBe(HttpStatusCode.OK, retired.Body.ToString());
	}
}
