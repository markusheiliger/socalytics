using System.Net;
using System.Text.Json;
using Shouldly;
using SocAlytics.Platform.Integration.Tests.Infrastructure;
using SocAlytics.Platform.Integration.Tests.Recordings;
using SocAlytics.Platform.Integration.Tests.Recordings.Support;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using SocAlytics.Platform.Application.Analysis.Commands;
using SocAlytics.Platform.Application.Recordings;
using SocAlytics.Platform.Application.Registry;
using SocAlytics.Platform.Domain.Analysis;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.Analysis;

[Collection("NatsFaultInjection")]
public sealed class RunCreationTests(PostgresContainerFixture postgres, RustFsContainerFixture rustFs, NatsContainerFixture nats)
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

	private sealed class SwitchLookup(IRecordingSetLookup inner, SwitchLookup.Flag flag) : IRecordingSetLookup
	{
		public sealed class Flag { public volatile bool Throw; }

		public Task<RecordingSetLineage?> GetAsync(Guid id, CancellationToken ct) =>
			flag.Throw ? throw new InvalidOperationException("unavailable") : inner.GetAsync(id, ct);
	}

	private static Dictionary<string, string?> DevWorkflow()
	{
		var dir = new DirectoryInfo(AppContext.BaseDirectory);
		while (!File.Exists(Path.Combine(dir!.FullName, "src", "platform", "SocAlytics.Platform.Api", "appsettings.Development.json")))
		{
			dir = dir.Parent;
		}

		var cfg = new ConfigurationBuilder().AddJsonFile(Path.Combine(dir.FullName, "src", "platform", "SocAlytics.Platform.Api", "appsettings.Development.json")).Build();
		return cfg.GetSection("Analysis").AsEnumerable(true).ToDictionary(p => "Analysis:" + p.Key, p => p.Value);
	}

	private async Task<(RecordingsFinalizedNotification Notification, FinalizationSetup Setup)> StartAsync(
		Dictionary<string, string?> workflow, CancellationToken ct, SwitchLookup.Flag? flag = null)
	{
		var setup = await FinalizationSetup.StartAsync(postgres, rustFs, ct, sets: null, configureServices: services =>
		{
			if (flag is null)
			{
				return;
			}

			var original = services.Last(d => d.ServiceType == typeof(IRecordingSetLookup));
			services.Remove(original);
			services.AddScoped<IRecordingSetLookup>(sp => new SwitchLookup(
				(IRecordingSetLookup)ActivatorUtilities.CreateInstance(sp, original.ImplementationType!), flag));
		}, extraSettings: workflow);
		var set = await RecordingFinalizationHelper.FinalizeAsync(setup.Coach, setup.Hierarchy.MatchId, 1, ct);
		await using var scope = setup.Factory.Services.CreateAsyncScope();
		var lineage = (await scope.ServiceProvider.GetRequiredService<IRecordingSetLookup>().GetAsync(set.RecordingSetVersionId, ct))!;
		return (new RecordingsFinalizedNotification(
			Guid.NewGuid(), "1.0.0", DateTimeOffset.UtcNow, lineage.RecordingSetVersionId, lineage.MatchId, lineage.TeamId,
			lineage.Members.Select(m => new FinalizedNotificationMember(m.Position, m.RecordingVersionId, m.RecordingContentDigest, m.TimelineMappingId, m.TimelineMappingDigest)).ToList()), setup);
	}

	private static async Task<NotificationOutcome> HandleAsync(FinalizationSetup s, RecordingsFinalizedNotification n, CancellationToken ct)
	{
		await using var scope = s.Factory.Services.CreateAsyncScope();
		return await scope.ServiceProvider.GetRequiredService<HandleRecordingsFinalizedNotificationHandler>().HandleAsync(n, ct);
	}

	[Fact]
	public async Task ConcurrentDuplicateDeliveriesCreateOneRun()
	{
		var ct = TestContext.Current.CancellationToken;
		var (n, s) = await StartAsync(DevWorkflow(), ct);
		await using var _ = s;
		var outcomes = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => Task.Run(() => HandleAsync(s, n, ct), ct)));
		outcomes.ShouldAllBe(o => o is NotificationOutcome.RunCreated || o is NotificationOutcome.RunExists);
		(await s.ScalarAsync("SELECT count(*) FROM socalytics.analysis_runs", ct)).ShouldBe(1);
		(await s.ScalarAsync("SELECT count(*) FROM socalytics.analysis_logical_jobs", ct)).ShouldBe(3);
		(await s.ScalarAsync("SELECT count(*) FROM socalytics.analysis_logical_jobs WHERE segment_number = 1 AND recording_version_id IS NOT NULL", ct)).ShouldBe(1);
	}

	private sealed class CoverageResolver(IWorkflowDefinitionResolver inner, CoverageResolver.Setting setting) : IWorkflowDefinitionResolver
	{
		public sealed class Setting { public volatile CoveragePolicy Policy = CoveragePolicy.Complete; }

		public async Task<WorkflowResolution> ResolveDefaultAsync(CancellationToken ct)
		{
			var resolution = await inner.ResolveDefaultAsync(ct);
			if (resolution is not WorkflowResolution.ResolvedResult resolved)
			{
				return resolution;
			}

			var definition = resolved.Definition;
			return WorkflowResolution.Resolved(definition with
			{
				Edges = definition.Edges.Select(e => e with { CoveragePolicy = setting.Policy }).ToList(),
			});
		}
	}

	private async Task<(RecordingsFinalizedNotification Notification, FinalizationSetup Setup)> StartWithCoverageAsync(
		CoverageResolver.Setting setting, CancellationToken ct)
	{
		var setup = await FinalizationSetup.StartAsync(postgres, rustFs, ct, sets: null, configureServices: services =>
		{
			var original = services.Last(d => d.ServiceType == typeof(IWorkflowDefinitionResolver));
			services.Remove(original);
			services.AddSingleton<IWorkflowDefinitionResolver>(sp => new CoverageResolver(
				(IWorkflowDefinitionResolver)ActivatorUtilities.CreateInstance(sp, original.ImplementationType!), setting));
		}, extraSettings: DevWorkflow());
		var set = await RecordingFinalizationHelper.FinalizeAsync(setup.Coach, setup.Hierarchy.MatchId, 1, ct);
		await using var scope = setup.Factory.Services.CreateAsyncScope();
		var lineage = (await scope.ServiceProvider.GetRequiredService<IRecordingSetLookup>().GetAsync(set.RecordingSetVersionId, ct))!;
		return (new RecordingsFinalizedNotification(
			Guid.NewGuid(), "1.0.0", DateTimeOffset.UtcNow, lineage.RecordingSetVersionId, lineage.MatchId, lineage.TeamId,
			lineage.Members.Select(m => new FinalizedNotificationMember(m.Position, m.RecordingVersionId, m.RecordingContentDigest, m.TimelineMappingId, m.TimelineMappingDigest)).ToList()), setup);
	}

	private static async Task<string> SnapshotAsync(FinalizationSetup s, string column, CancellationToken ct)
	{
		await using var connection = new NpgsqlConnection(s.Db.MigratorConnectionString);
		await connection.OpenAsync(ct);
		await using var command = new NpgsqlCommand($"SELECT {column} FROM socalytics.analysis_run_snapshots", connection);
		return (string)(await command.ExecuteScalarAsync(ct))!;
	}

	[Fact]
	public async Task RunCopiesThePartialEdgePolicyIntoTheEdgeRowAndTheSnapshot()
	{
		var ct = TestContext.Current.CancellationToken;
		var setting = new CoverageResolver.Setting { Policy = CoveragePolicy.Partial(0.5m) };
		var (n, s) = await StartWithCoverageAsync(setting, ct);
		await using var _ = s;
		(await HandleAsync(s, n, ct)).ShouldBeOfType<NotificationOutcome.RunCreated>();
		(await s.ScalarAsync("SELECT count(*) FROM socalytics.analysis_dependency_edges WHERE coverage_mode = 'partial' AND minimum_accepted_fraction = 0.5", ct)).ShouldBe(2);
		(await s.ScalarAsync("SELECT count(*) FROM socalytics.analysis_dependency_edges", ct)).ShouldBe(2);
		using var snapshot = JsonDocument.Parse(await SnapshotAsync(s, "snapshot::text", ct));
		var coverage = snapshot.RootElement.GetProperty("edges")[0].GetProperty("coverage");
		coverage.GetProperty("mode").GetString().ShouldBe("partial");
		coverage.GetProperty("minimumAcceptedFraction").GetString().ShouldBe("0.5");
	}

	[Fact]
	public async Task WorkflowWithoutPartialEdgesKeepsItsPreviousSnapshotShape()
	{
		var ct = TestContext.Current.CancellationToken;
		var (n, s) = await StartWithCoverageAsync(new CoverageResolver.Setting(), ct);
		await using var _ = s;
		(await HandleAsync(s, n, ct)).ShouldBeOfType<NotificationOutcome.RunCreated>();
		(await s.ScalarAsync("SELECT count(*) FROM socalytics.analysis_dependency_edges WHERE coverage_mode = 'complete' AND minimum_accepted_fraction IS NULL", ct)).ShouldBe(2);
		var json = await SnapshotAsync(s, "snapshot::text", ct);
		json.ShouldNotContain("coverage");

		await using var scope = s.Factory.Services.CreateAsyncScope();
		var definition = ((WorkflowResolution.ResolvedResult)await scope.ServiceProvider.GetRequiredService<IWorkflowDefinitionResolver>().ResolveDefaultAsync(ct)).Definition;
		var mapper = scope.ServiceProvider.GetRequiredService<SocAlytics.Platform.Application.Analysis.WorkflowSnapshotMapper>();
		var complete = mapper.Map(definition);
		complete.RunSnapshotJson.ShouldNotContain("coverage");
		(await SnapshotAsync(s, "snapshot_digest", ct)).ShouldBe(complete.RunSnapshotDigest);
		mapper.Map(definition with { Edges = definition.Edges.Select(e => e with { CoveragePolicy = CoveragePolicy.Partial() }).ToList() })
			.RunSnapshotDigest.ShouldNotBe(complete.RunSnapshotDigest);
	}

	[Fact]
	public async Task LaterRegistryChangeDoesNotAlterAnExistingRun()
	{
		var ct = TestContext.Current.CancellationToken;
		var setting = new CoverageResolver.Setting { Policy = CoveragePolicy.Partial(0.5m) };
		var (n, s) = await StartWithCoverageAsync(setting, ct);
		await using var _ = s;
		(await HandleAsync(s, n, ct)).ShouldBeOfType<NotificationOutcome.RunCreated>();
		var digest = await SnapshotAsync(s, "snapshot_digest", ct);

		setting.Policy = CoveragePolicy.Complete;
		(await HandleAsync(s, n with { EventId = Guid.NewGuid() }, ct)).ShouldBeOfType<NotificationOutcome.RunExists>();
		(await s.ScalarAsync("SELECT count(*) FROM socalytics.analysis_runs", ct)).ShouldBe(1);
		(await s.ScalarAsync("SELECT count(*) FROM socalytics.analysis_dependency_edges WHERE coverage_mode = 'partial' AND minimum_accepted_fraction = 0.5", ct)).ShouldBe(2);
		(await SnapshotAsync(s, "snapshot_digest", ct)).ShouldBe(digest);
		await using var scope = s.Factory.Services.CreateAsyncScope();
		var run = await scope.ServiceProvider.GetRequiredService<SocAlytics.Platform.Application.Analysis.Stores.IAnalysisRunStore>()
			.FindRunIdAsync(n.RecordingSetVersionId, "development-default", "1", ct);
		run.ShouldNotBeNull();
	}

	[Fact]
	public async Task MismatchedNotificationsAreRejectedWithoutARun()
	{
		var ct = TestContext.Current.CancellationToken;
		var (n, s) = await StartAsync(DevWorkflow(), ct);
		await using var _ = s;
		(await HandleAsync(s, n with { EventId = Guid.NewGuid(), RecordingSetVersionId = Guid.NewGuid() }, ct))
			.ShouldBeOfType<NotificationOutcome.Rejected>().Category.ShouldBe("lineage-not-found");
		(await HandleAsync(s, n with { EventId = Guid.NewGuid(), TeamId = Guid.NewGuid() }, ct))
			.ShouldBeOfType<NotificationOutcome.Rejected>().Category.ShouldBe("scope-mismatch");
		var wrongVersion = n.Members.Select(m => m with { RecordingVersionId = Guid.NewGuid() }).ToList();
		(await HandleAsync(s, n with { EventId = Guid.NewGuid(), Members = wrongVersion }, ct))
			.ShouldBeOfType<NotificationOutcome.Rejected>().Category.ShouldBe("lineage-mismatch");
		var wrongDigest = n.Members.Select(m => m with { TimelineMappingDigest = m.TimelineMappingDigest[..^1] + (m.TimelineMappingDigest.EndsWith('0') ? "1" : "0") }).ToList();
		(await HandleAsync(s, n with { EventId = Guid.NewGuid(), Members = wrongDigest }, ct))
			.ShouldBeOfType<NotificationOutcome.Rejected>().Category.ShouldBe("lineage-mismatch");
		(await s.ScalarAsync("SELECT count(*) FROM socalytics.analysis_notification_receipts WHERE outcome = 'rejected'", ct)).ShouldBe(4);
		(await s.ScalarAsync("SELECT count(*) FROM socalytics.analysis_runs", ct)).ShouldBe(0);
	}

	[Fact]
	public async Task ThrowingLookupLeavesNoReceipt()
	{
		var ct = TestContext.Current.CancellationToken;
		var flag = new SwitchLookup.Flag();
		var (n, s) = await StartAsync(DevWorkflow(), ct, flag);
		await using var _ = s;
		flag.Throw = true;
		(await HandleAsync(s, n, ct)).ShouldBeOfType<NotificationOutcome.DependencyUnavailable>();
		(await s.ScalarAsync("SELECT count(*) FROM socalytics.analysis_notification_receipts", ct)).ShouldBe(0);
	}

	[Theory]
	[InlineData("dev-match-node", "dev-segment-node")]
	[InlineData("dev-segment-node", "dev-segment-node")]
	public async Task InvalidGraphYieldsFailedRunWithoutChildren(string upstream, string dependent)
	{
		var ct = TestContext.Current.CancellationToken;
		var workflow = DevWorkflow();
		workflow["Analysis:DefaultWorkflow:Edges:2:UpstreamNodeKey"] = upstream;
		workflow["Analysis:DefaultWorkflow:Edges:2:DependentNodeKey"] = dependent;
		workflow["Analysis:DefaultWorkflow:Edges:2:Kind"] = "required";
		var (n, s) = await StartAsync(workflow, ct);
		await using var _ = s;
		(await HandleAsync(s, n, ct)).ShouldBeOfType<NotificationOutcome.RunCreated>();
		(await s.ScalarAsync("SELECT count(*) FROM socalytics.analysis_runs WHERE state = 'Failed' AND failure_reason LIKE 'invalid-graph:%' AND terminated_at IS NOT NULL", ct)).ShouldBe(1);
		(await s.ScalarAsync("SELECT (SELECT count(*) FROM socalytics.analysis_workflow_nodes) + (SELECT count(*) FROM socalytics.analysis_dependency_edges) + (SELECT count(*) FROM socalytics.analysis_logical_jobs) + (SELECT count(*) FROM socalytics.outbox_messages WHERE message_type = 'analysis.ready-work')", ct)).ShouldBe(0);
	}

	private Dictionary<string, string?> WorkerSettings()
	{
		var w = DevWorkflow();
		w["ConnectionStrings:nats"] = nats.ConnectionString;
		w["Messaging:Nats:ProvisionTopology"] = "true";
		w["Analysis:Workers:Enabled"] = "true";
		w["Analysis:Notifications:NakDelaySeconds"] = "1";
		return w;
	}

	private static async Task<bool> WaitAsync(Func<Task<bool>> condition, CancellationToken ct)
	{
		for (var i = 0; i < 120; i++)
		{
			if (await condition())
			{
				return true;
			}

			await Task.Delay(500, ct);
		}

		return false;
	}

	private async Task PublishAsync(byte[] payload, CancellationToken ct)
	{
		await using var connection = new NATS.Client.Core.NatsConnection(NATS.Client.Core.NatsOpts.Default with { Url = nats.ConnectionString });
		var js = new NATS.Client.JetStream.NatsJSContext(connection);
		var ack = await js.PublishAsync("matches.recordings-finalized", payload,
			serializer: NATS.Client.Core.NatsRawSerializer<byte[]>.Default,
			headers: new NATS.Client.Core.NatsHeaders { ["Nats-Msg-Id"] = Guid.NewGuid().ToString() }, cancellationToken: ct);
		ack.Error.ShouldBeNull();
	}

	[Fact]
	public async Task FinalizedRecordingSetFlowsThroughNatsToExactlyOneRun()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var s = await FinalizationSetup.StartAsync(postgres, rustFs, ct, sets: null, extraSettings: WorkerSettings());
		await RecordingFinalizationHelper.FinalizeAsync(s.Coach, s.Hierarchy.MatchId, 1, ct);
		(await WaitAsync(async () => await s.ScalarAsync("SELECT count(*) FROM socalytics.analysis_runs", ct) == 1, ct)).ShouldBeTrue();
		(await s.ScalarAsync("SELECT count(*) FROM socalytics.outbox_messages WHERE message_type = 'matches.recordings-finalized' AND state = 'published'", ct)).ShouldBe(1);

		await using var connection = new NpgsqlConnection(s.Db.MigratorConnectionString);
		await connection.OpenAsync(ct);
		await using var cmd = new NpgsqlCommand("SELECT payload::text FROM socalytics.recording_finalized_events", connection);
		var payload = System.Text.Encoding.UTF8.GetBytes((string)(await cmd.ExecuteScalarAsync(ct))!);
		await PublishAsync(payload, ct);
		await PublishAsync(payload, ct);
		await Task.Delay(TimeSpan.FromSeconds(4), ct);
		(await s.ScalarAsync("SELECT count(*) FROM socalytics.analysis_runs", ct)).ShouldBe(1);
		(await s.ScalarAsync("SELECT count(*) FROM socalytics.analysis_notification_receipts", ct)).ShouldBe(1);
	}

	[Fact]
	public async Task UnavailableLookupNaksAndRunIsCreatedAfterRecovery()
	{
		var ct = TestContext.Current.CancellationToken;
		var flag = new SwitchLookup.Flag { Throw = true };
		await using var s = await FinalizationSetup.StartAsync(postgres, rustFs, ct, sets: null, configureServices: services =>
		{
			var original = services.Last(d => d.ServiceType == typeof(IRecordingSetLookup));
			services.Remove(original);
			services.AddScoped<IRecordingSetLookup>(sp => new SwitchLookup(
				(IRecordingSetLookup)ActivatorUtilities.CreateInstance(sp, original.ImplementationType!), flag));
		}, extraSettings: WorkerSettings());
		await RecordingFinalizationHelper.FinalizeAsync(s.Coach, s.Hierarchy.MatchId, 1, ct);
		(await WaitAsync(async () => await s.ScalarAsync("SELECT count(*) FROM socalytics.outbox_messages WHERE message_type = 'matches.recordings-finalized' AND state = 'published'", ct) == 1, ct)).ShouldBeTrue();
		await Task.Delay(TimeSpan.FromSeconds(3), ct);
		(await s.ScalarAsync("SELECT count(*) FROM socalytics.analysis_notification_receipts", ct)).ShouldBe(0);
		flag.Throw = false;
		(await WaitAsync(async () => await s.ScalarAsync("SELECT count(*) FROM socalytics.analysis_runs", ct) == 1, ct)).ShouldBeTrue();
	}

	[Fact]
	public async Task SchemaInvalidMessageIsRejectedAndAcknowledged()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var s = await FinalizationSetup.StartAsync(postgres, rustFs, ct, sets: null, extraSettings: WorkerSettings());
		var id = Guid.NewGuid();
		await PublishAsync(System.Text.Encoding.UTF8.GetBytes($"{{\"eventId\":\"{id}\"}}"), ct);
		(await WaitAsync(async () => await s.ScalarAsync($"SELECT count(*) FROM socalytics.analysis_notification_receipts WHERE message_id = '{id}' AND outcome = 'rejected' AND rejection_category = 'schema-invalid'", ct) == 1, ct)).ShouldBeTrue();
		(await s.ScalarAsync("SELECT count(*) FROM socalytics.analysis_runs", ct)).ShouldBe(0);
	}
}
