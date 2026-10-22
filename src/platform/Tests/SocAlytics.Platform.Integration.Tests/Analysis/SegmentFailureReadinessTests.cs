using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Application.Analysis;
using SocAlytics.Platform.Application.Analysis.Commands;
using SocAlytics.Platform.Application.Analysis.Stores;
using SocAlytics.Platform.Application.Recordings;
using SocAlytics.Platform.Application.Segments;
using SocAlytics.Platform.Infrastructure.Contracts;
using SocAlytics.Platform.Integration.Tests.Infrastructure;
using SocAlytics.Platform.Integration.Tests.Recordings;
using SocAlytics.Platform.Integration.Tests.Recordings.Support;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.Analysis;

[Collection("NatsFaultInjection")]
public sealed class SegmentFailureReadinessTests(PostgresContainerFixture postgres, RustFsContainerFixture rustFs)
{
	private const string Sha = "sha-256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

	private static Dictionary<string, string?> Workflow(string videoPolicy)
	{
		var dir = new DirectoryInfo(AppContext.BaseDirectory);
		while (!File.Exists(Path.Combine(dir!.FullName, "src", "platform", "SocAlytics.Platform.Api", "appsettings.Development.json")))
		{
			dir = dir.Parent;
		}

		var cfg = new ConfigurationBuilder()
			.AddJsonFile(Path.Combine(dir.FullName, "src", "platform", "SocAlytics.Platform.Api", "appsettings.Development.json")).Build();
		var values = cfg.GetSection("Analysis").AsEnumerable(true).ToDictionary(p => "Analysis:" + p.Key, p => p.Value);
		values["Analysis:DefaultWorkflow:Nodes:0:VideoPolicy"] = videoPolicy;
		return values;
	}

	private async Task<(FinalizationSetup Setup, Guid RunId)> CreateRunAsync(string videoPolicy, CancellationToken ct)
	{
		var setup = await FinalizationSetup.StartAsync(postgres, rustFs, ct, sets: null, extraSettings: Workflow(videoPolicy));
		var set = await RecordingFinalizationHelper.FinalizeAsync(setup.Coach, setup.Hierarchy.MatchId, 1, ct);
		await using var scope = setup.Factory.Services.CreateAsyncScope();
		var lineage = (await scope.ServiceProvider.GetRequiredService<IRecordingSetLookup>().GetAsync(set.RecordingSetVersionId, ct))!;
		var notification = new RecordingsFinalizedNotification(
			Guid.NewGuid(), "1.0.0", DateTimeOffset.UtcNow, lineage.RecordingSetVersionId, lineage.MatchId, lineage.TeamId,
			lineage.Members.Select(m => new FinalizedNotificationMember(
				m.Position, m.RecordingVersionId, m.RecordingContentDigest, m.TimelineMappingId, m.TimelineMappingDigest)).ToList());
		var outcome = await scope.ServiceProvider.GetRequiredService<HandleRecordingsFinalizedNotificationHandler>().HandleAsync(notification, ct);
		outcome.ShouldBeOfType<NotificationOutcome.RunCreated>();
		var runId = Guid.Parse((await QueryAsync(setup, "SELECT id::text FROM socalytics.analysis_runs", ct))!);
		return (setup, runId);
	}

	private static async Task<string?> QueryAsync(FinalizationSetup s, string sql, CancellationToken ct)
	{
		await using var connection = new NpgsqlConnection(s.Db.MigratorConnectionString);
		await connection.OpenAsync(ct);
		await using var command = new NpgsqlCommand(sql, connection);
		return (await command.ExecuteScalarAsync(ct))?.ToString();
	}

	private const string SegmentJob =
		"FROM socalytics.analysis_logical_jobs j JOIN socalytics.analysis_workflow_nodes n ON n.id = j.node_id WHERE n.execution_scope = 'segment'";

	private static async Task<JsonElement> ReadyPayloadAsync(FinalizationSetup s, CancellationToken ct) =>
		JsonDocument.Parse((await QueryAsync(
			s,
			"SELECT o.payload::text FROM socalytics.outbox_messages o JOIN socalytics.analysis_logical_jobs j ON j.id = o.logical_job_id " +
			"JOIN socalytics.analysis_workflow_nodes n ON n.id = j.node_id WHERE o.message_type = 'analysis.ready-work' AND n.execution_scope = 'segment' LIMIT 1",
			ct))!).RootElement.Clone();

	private async Task FailSegmentAsync(FinalizationSetup s, Guid runId, CancellationToken ct)
	{
		await using var scope = s.Factory.Services.CreateAsyncScope();
		var sp = scope.ServiceProvider;
		var store = sp.GetRequiredService<ISegmentStore>();
		await using var uow = await sp.GetRequiredService<IUnitOfWork>().BeginAsync(ct);
		var segment = (await store.ClaimDueAsync("test", TimeSpan.FromMinutes(5), 10, ct)).Single().Segment;
		(await store.TryFailAsync(segment.Id, segment.CandidateId!.Value, "retry-budget-exhausted", ct)).ShouldBeTrue();
		(await store.ListWaitingJobsAsync(segment.Identity, ct)).Select(w => w.RunId).Distinct().Single().ShouldBe(runId);
		await sp.GetRequiredService<IAnalysisRunStore>().LockRunAsync(runId, ct);
		await sp.GetRequiredService<RunReadinessCoordinator>().ApplyAsync(runId, ct);
		await uow.CommitAsync(ct);
	}

	[Fact]
	public async Task FailedSegmentFailsTheRequiredJobWithSegmentUnavailable()
	{
		var ct = TestContext.Current.CancellationToken;
		var (s, runId) = await CreateRunAsync("required", ct);
		await using var setup = s;

		await FailSegmentAsync(s, runId, ct);

		(await QueryAsync(s, "SELECT j.state || ':' || j.failure_reason " + SegmentJob, ct)).ShouldBe("Failed:segment-unavailable");
		(await QueryAsync(s, "SELECT count(*) FROM socalytics.outbox_messages WHERE message_type = 'analysis.ready-work'", ct)).ShouldBe("0");
	}

	[Fact]
	public async Task FailedSegmentMakesTheOptionalJobReadyWithoutMedia()
	{
		var ct = TestContext.Current.CancellationToken;
		var (s, runId) = await CreateRunAsync("optional", ct);
		await using var setup = s;

		await FailSegmentAsync(s, runId, ct);

		(await QueryAsync(s, "SELECT j.state " + SegmentJob, ct)).ShouldBe("Ready");
		var payload = await ReadyPayloadAsync(s, ct);
		payload.TryGetProperty("segmentMedia", out _).ShouldBeFalse();
		payload.GetProperty("contractVersion").GetString().ShouldBe("1.1.0");
		new JsonSchemaContractValidator().Validate(ContractSchemaIds.AnalystJobV1, payload).IsValid.ShouldBeTrue();
	}

	[Fact]
	public async Task RejectedMaterializationRequestFailsTheRequiredJob()
	{
		var ct = TestContext.Current.CancellationToken;
		var (s, runId) = await CreateRunAsync("required", ct);
		await using var setup = s;

		await using (var connection = new NpgsqlConnection(s.Db.MigratorConnectionString))
		{
			await connection.OpenAsync(ct);
			await using var command = new NpgsqlCommand(
				"UPDATE socalytics.analysis_logical_jobs SET segment_rejection_reason = 'segment-coverage-empty' WHERE execution_scope = 'segment'", connection);
			await command.ExecuteNonQueryAsync(ct);
		}

		await using (var scope = s.Factory.Services.CreateAsyncScope())
		{
			var sp = scope.ServiceProvider;
			await using var uow = await sp.GetRequiredService<IUnitOfWork>().BeginAsync(ct);
			await sp.GetRequiredService<IAnalysisRunStore>().LockRunAsync(runId, ct);
			await sp.GetRequiredService<RunReadinessCoordinator>().ApplyAsync(runId, ct);
			await uow.CommitAsync(ct);
		}

		(await QueryAsync(s, "SELECT j.state || ':' || j.failure_reason " + SegmentJob, ct)).ShouldBe("Failed:segment-unavailable");
	}
}
