using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;
using Shouldly;
using SocAlytics.Platform.Application.AgentOrchestration;
using SocAlytics.Platform.Application.Registry;
using SocAlytics.Platform.Integration.Tests.IdentityAccess.Support;
using SocAlytics.Platform.Integration.Tests.Infrastructure;
using SocAlytics.Platform.Integration.Tests.Recordings.Support;
using SocAlytics.Platform.Integration.Tests.Registry.Support;
using SocAlytics.Platform.Migrator;

namespace SocAlytics.Platform.Integration.Tests.AgentOrchestration.Support;

internal enum SeededRunState
{
	Completed,
	PartiallyCompleted,
	Failed,
	Running,
	Cancelled,
}

// A platform API with enabled agents, configured workload clients, and short lease and monitor values.
internal sealed class AgentOrchestrationFixture : IAsyncDisposable
{
	public const string DisabledClientId = "workload-disabled";

	private AgentOrchestrationFixture(
		IsolatedDatabase db, PlatformApiFactory factory, HttpClient anon, ApiSession admin, ClubHierarchy hierarchy,
		TestAgentWorkload workload, TestAgentWorkload disabledWorkload)
	{
		Db = db;
		Factory = factory;
		Anon = anon;
		Admin = admin;
		Hierarchy = hierarchy;
		Workload = workload;
		DisabledWorkload = disabledWorkload;
	}

	public IsolatedDatabase Db { get; }

	public PlatformApiFactory Factory { get; }

	public HttpClient Anon { get; }

	public ApiSession Admin { get; }

	public ClubHierarchy Hierarchy { get; }

	public TestAgentWorkload Workload { get; }

	public TestAgentWorkload DisabledWorkload { get; }

	public DateTimeOffset Now => Factory.Time.GetUtcNow();

	public static async Task<AgentOrchestrationFixture> StartAsync(
		PostgresContainerFixture postgres, CancellationToken ct, Action<Dictionary<string, string?>>? settings = null)
	{
		var db = await postgres.CreateDatabaseAsync(ct);
		(await MigratorHarness.RunAsync(db, TestMigrationCatalogs.Platform(), ct)).ExitCode.ShouldBe(MigratorExitCode.Success);
		var workload = new TestAgentWorkload();
		var disabled = new TestAgentWorkload(DisabledClientId);
		var extra = new Dictionary<string, string?>
		{
			["AgentOrchestration:Agents:Enabled:0"] = "specialist-tactics",
			["AgentOrchestration:Agents:Enabled:1"] = "specialist-video-analysis",
			["AgentOrchestration:Execution:LeaseSeconds"] = "2",
			["AgentOrchestration:Execution:MaxAttemptSeconds"] = "10",
			["AgentOrchestration:Workloads:TokenLifetime"] = "00:05:00",
			["AgentOrchestration:Workloads:Clients:0:ClientId"] = workload.ClientId,
			["AgentOrchestration:Workloads:Clients:0:PublicJwk"] = workload.PublicJwk,
			["AgentOrchestration:Workloads:Clients:0:Enabled"] = "true",
			["AgentOrchestration:Workloads:Clients:1:ClientId"] = disabled.ClientId,
			["AgentOrchestration:Workloads:Clients:1:PublicJwk"] = disabled.PublicJwk,
			["AgentOrchestration:Workloads:Clients:1:Enabled"] = "false",
			["AgentOrchestration:Monitor:MonitorInterval"] = "00:00:01",
			["AgentOrchestration:Monitor:ReconciliationInterval"] = "00:00:02",
			["AgentOrchestration:Monitor:RepublishBound"] = "00:00:05",
		};
		settings?.Invoke(extra);
		var factory = new PlatformApiFactory(
			db,
			new Dictionary<string, string?>
			{
				["ClubDisplayName"] = "Agent Club",
				["FirstClubAdmin:AccountName"] = "agent-admin",
				["FirstClubAdmin:InitialPassword"] = "Initial-Admin-Pass-1234",
			},
			configureServices: services => services.Configure<RegistryOptions>(o =>
			{
				o.SoftwareKeys = SoftwareKeyPolicy.All;
				o.RateLimits.PairingCreationPerMinute = 10_000;
				o.RateLimits.StatusPerMinute = 10_000;
				o.RateLimits.ActivationPerMinute = 10_000;
				o.RateLimits.CodeSubmissionPerMinute = 10_000;
				o.RateLimits.TokenPerMinute = 10_000;
			}),
			extraSettings: extra);
		factory.Time.Set(DateTimeOffset.UtcNow);
		await factory.WaitUntilHealthyAsync(ct);
		await TestMembers.SeedAsync(db, "admin-1", clubRoles: ["club-admin", "registrar"], cancellationToken: ct);
		var admin = await ApiSession.SignInAsync(factory, "admin-1", TestMembers.DefaultPassword, ct);
		var hierarchy = await new ClubHierarchyBuilder(factory, admin).CreateAsync(ct);
		return new AgentOrchestrationFixture(db, factory, factory.CreateApiClient(), admin, hierarchy, workload, disabled);
	}

	// Disables the configured client in the live options, as a configuration reload would.
	public void SetClientEnabled(string clientId, bool enabled) =>
		Factory.Services.GetRequiredService<IOptionsMonitor<AgentOrchestrationOptions>>().CurrentValue
			.Workloads.Clients.Single(c => c.ClientId == clientId).Enabled = enabled;

	public async Task<Guid> SeedRunAsync(SeededRunState state, Guid? matchId = null, CancellationToken ct = default)
	{
		var run = Guid.NewGuid();
		var terminal = state != SeededRunState.Running;
		var at = Now;
		await using var connection = new NpgsqlConnection(Db.MigratorConnectionString);
		await connection.OpenAsync(ct);
		await using var command = new NpgsqlCommand(
			"""
			INSERT INTO socalytics.analysis_runs (id, team_id, match_id, recording_set_version_id, recording_set_finalized_at, workflow_id, workflow_version, requested_capabilities, completion_policy, state, created_at, running_at, terminated_at, updated_at, cancellation_cause, cancelled_by_account_id)
			VALUES (@run, @team, @match, @set, now(), 'wf', '1.0.0', ARRAY['cap-a'], 'all-requested-capabilities', @state, @at, @at, @terminated, @at,
			        CASE WHEN @state = 'Cancelled' THEN 'member' END,
			        CASE WHEN @state = 'Cancelled' THEN (SELECT id FROM socalytics.member_account ORDER BY created_at LIMIT 1) END);
			INSERT INTO socalytics.analysis_run_snapshots (run_id, snapshot, snapshot_digest) VALUES (@run, '{}'::jsonb, 'sha-256:a');
			""",
			connection);
		command.Parameters.AddWithValue("run", run);
		command.Parameters.AddWithValue("team", Hierarchy.TeamId);
		command.Parameters.AddWithValue("match", matchId ?? Hierarchy.MatchId);
		command.Parameters.AddWithValue("set", Guid.NewGuid());
		command.Parameters.AddWithValue("state", state.ToString());
		command.Parameters.AddWithValue("at", at);
		command.Parameters.AddWithValue("terminated", terminal ? at : DBNull.Value);
		await command.ExecuteNonQueryAsync(ct);
		return run;
	}

	// A new match of the same team with a seeded run in the given state.
	public async Task<(Guid MatchId, Guid RunId)> CreateMatchWithRunAsync(SeededRunState state, CancellationToken ct)
	{
		var matchId = await new ClubHierarchyBuilder(Factory, Admin).CreateMatchAsync(Hierarchy.TeamId, ct);
		return (matchId, await SeedRunAsync(state, matchId, ct));
	}

	// Registers, approves, and activates an Analyst Manager and returns its access token and key.
	public async Task<(TestAnalystManager Manager, string Token)> ActivateManagerAsync(CancellationToken ct)
	{
		const string origin = TestAgentWorkload.Origin;
		const string tokenPath = "/api/v1/analyst-managers/token";
		var manager = new TestAnalystManager();
		using var pair = await RegistryTestHost.PairAsync(Anon, RegistryTestHost.PairingBody(manager), ct);
		var root = JsonDocument.Parse(await pair.Content.ReadAsStringAsync(ct)).RootElement;
		var handle = root.GetProperty("pollingHandle").GetString()!;
		using var submitted = await Admin.SendAsync(HttpMethod.Post, "/api/v1/analyst-manager-registrations",
			new { pairingCode = root.GetProperty("pairingCode").GetString(), deviceFingerprint = root.GetProperty("deviceFingerprint").GetString() }, ct);
		var id = JsonDocument.Parse(await submitted.Content.ReadAsStringAsync(ct)).RootElement.GetProperty("registrationId").GetGuid();
		using var approved = await Admin.SendAsync(HttpMethod.Post, $"/api/v1/analyst-manager-registrations/{id}/approval", null, ct);
		approved.StatusCode.ShouldBe(HttpStatusCode.OK);
		using var status = await RegistryTestHost.StatusAsync(Anon, handle, ct);
		var challenge = JsonDocument.Parse(await status.Content.ReadAsStringAsync(ct)).RootElement.GetProperty("challenge").GetString()!;
		var proof = manager.ActivationProof(Now, origin + "/api/v1/analyst-managers/pairings/activation", challenge);
		using var activated = await Anon.PostAsJsonAsync("/api/v1/analyst-managers/pairings/activation", new { pollingHandle = handle, proof }, ct);
		activated.StatusCode.ShouldBe(HttpStatusCode.OK);
		var clientId = JsonDocument.Parse(await activated.Content.ReadAsStringAsync(ct)).RootElement.GetProperty("managerId").GetString()!;

		var request = new HttpRequestMessage(HttpMethod.Post, tokenPath)
		{
			Content = new FormUrlEncodedContent(new Dictionary<string, string>
			{
				["grant_type"] = "client_credentials",
				["client_assertion_type"] = TestAgentWorkload.AssertionType,
				["client_assertion"] = manager.ClientAssertion(Now, origin + tokenPath, clientId),
				["client_id"] = clientId,
			}),
		};
		request.Headers.Add("DPoP", manager.DPoPProof(Now, "POST", origin + tokenPath));
		using var token = await Anon.SendAsync(request, ct);
		token.StatusCode.ShouldBe(HttpStatusCode.OK);
		return (manager, JsonDocument.Parse(await token.Content.ReadAsStringAsync(ct)).RootElement.GetProperty("access_token").GetString()!);
	}

	public async Task<long> ScalarAsync(string sql, CancellationToken ct)
	{
		await using var connection = new NpgsqlConnection(Db.MigratorConnectionString);
		await connection.OpenAsync(ct);
		await using var command = new NpgsqlCommand(sql, connection);
		return Convert.ToInt64(await command.ExecuteScalarAsync(ct));
	}

	public async ValueTask DisposeAsync()
	{
		Admin.Dispose();
		Anon.Dispose();
		Workload.Dispose();
		DisabledWorkload.Dispose();
		await Factory.DisposeAsync();
		await Db.DisposeAsync();
	}
}
