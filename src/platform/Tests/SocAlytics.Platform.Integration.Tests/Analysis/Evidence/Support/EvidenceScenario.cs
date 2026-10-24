using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Application.Analysis.Commands;
using SocAlytics.Platform.Application.Analysis.Stores;
using SocAlytics.Platform.Application.Recordings;
using SocAlytics.Platform.Application.Segments;
using SocAlytics.Platform.Integration.Tests.IdentityAccess.Support;
using SocAlytics.Platform.Integration.Tests.Infrastructure;
using SocAlytics.Platform.Integration.Tests.Recordings;
using SocAlytics.Platform.Integration.Tests.Recordings.Support;

namespace SocAlytics.Platform.Integration.Tests.Analysis.Evidence.Support;

/// <summary>
/// A club, team, and match with members per role, a finalized set of the synthetic recording (and optionally a second
/// overlapping one), and a run whose indexed facts lie inside one segment and across a segment boundary under a
/// 5-second test policy. The API host's captured logs are available through <see cref="Logs"/>.
/// </summary>
internal sealed class EvidenceScenario : IAsyncDisposable
{
	public const int PolicySeconds = 5;

	public const int SegmentCount = SyntheticMedia.DurationSeconds / PolicySeconds;

	/// <summary>The fact inside the first segment (1.0-2.0 s).</summary>
	public const int InsideFactOrdinal = 0;

	public const long InsideFactStartMs = 1_000;

	public const long InsideFactEndMs = 2_000;

	/// <summary>The fact across the boundary of the first and second segment (4.0-6.0 s).</summary>
	public const int BoundaryFactOrdinal = 1;

	public const long BoundaryFactStartMs = 4_000;

	public const long BoundaryFactEndMs = 6_000;

	private const string CapabilityId = "cap.detect";

	private static readonly string DigestA = "sha-256:" + new string('a', 64);

	private static readonly string DigestB = "sha-256:" + new string('b', 64);

	private readonly SyntheticMedia media;

	private readonly string workingDirectory;

	private EvidenceScenario(
		FinalizationSetup setup,
		SyntheticMedia media,
		string workingDirectory,
		ApiSession viewer,
		ApiSession outsider,
		IReadOnlyList<UploadedSyntheticRecording> recordings,
		Guid recordingSetVersionId,
		Guid runId,
		Guid resultId)
	{
		Setup = setup;
		this.media = media;
		this.workingDirectory = workingDirectory;
		Viewer = viewer;
		Outsider = outsider;
		Recordings = recordings;
		RecordingSetVersionId = recordingSetVersionId;
		RunId = runId;
		ResultId = resultId;
	}

	public FinalizationSetup Setup { get; }

	public PlatformApiFactory Factory => Setup.Factory;

	public CapturingLoggerProvider Logs => Setup.Factory.Logs;

	public ApiSession Admin => Setup.Admin;

	public ApiSession Coach => Setup.Coach;

	public ApiSession Viewer { get; }

	/// <summary>An active member without any role on the team.</summary>
	public ApiSession Outsider { get; }

	public Guid TeamId => Setup.Hierarchy.TeamId;

	public Guid MatchId => Setup.Hierarchy.MatchId;

	public IReadOnlyList<UploadedSyntheticRecording> Recordings { get; }

	public Guid RecordingSetVersionId { get; }

	public Guid RunId { get; }

	public Guid ResultId { get; }

	public string EvidencePath => $"/api/v1/analysis/runs/{RunId}/evidence-media";

	public static async Task<EvidenceScenario> StartAsync(
		PostgresContainerFixture postgres,
		RustFsContainerFixture rustFs,
		CancellationToken ct,
		bool secondRecording = false,
		Dictionary<string, string?>? extraSettings = null,
		Action<IServiceCollection>? configureServices = null)
	{
		var workingDirectory = Path.Combine(Path.GetTempPath(), $"socalytics-evidence-tests-{Guid.NewGuid():N}");
		var media = SyntheticMedia.Create();
		FinalizationSetup? setup = null;
		try
		{
			var settings = Settings(workingDirectory);
			foreach (var (key, value) in extraSettings ?? [])
			{
				settings[key] = value;
			}

			setup = await FinalizationSetup.StartAsync(postgres, rustFs, ct, extraSettings: settings, configureServices: configureServices);
			var viewer = await MemberAsync(setup, "viewer-1", "viewer", ct);
			var outsider = await MemberAsync(setup, "outsider-1", null, ct);

			var recordings = new List<UploadedSyntheticRecording>
			{
				await media.UploadAndFinalizeAsync(setup.Coach, setup.Hierarchy.MatchId, SyntheticTimeline.SingleSpan, ct),
			};
			var setVersionId = recordings[0].RecordingSetVersionId;
			if (secondRecording)
			{
				recordings.Add(await media.UploadAndFinalizeAsync(setup.Coach, setup.Hierarchy.MatchId, SyntheticTimeline.SingleSpan, ct));
				setVersionId = await FinalizeSetAsync(setup.Coach, setup.Hierarchy.MatchId, recordings, ct);
			}

			var runId = await CreateRunAsync(setup, setVersionId, ct);
			var resultId = await SeedFactsAsync(setup, runId, ct);
			return new EvidenceScenario(setup, media, workingDirectory, viewer, outsider, recordings, setVersionId, runId, resultId);
		}
		catch
		{
			if (setup is not null)
			{
				await setup.DisposeAsync();
			}

			media.Dispose();
			DeleteDirectory(workingDirectory);
			throw;
		}
	}

	/// <summary>Materializes every demanded segment through the Segment Service worker handler and returns the outcomes.</summary>
	public async Task<IReadOnlyList<SegmentMaterializationOutcome>> MaterializeSegmentsAsync(CancellationToken ct)
	{
		var outcomes = new List<SegmentMaterializationOutcome>();
		for (var round = 0; round < 10; round++)
		{
			await using var scope = Factory.Services.CreateAsyncScope();
			var handler = scope.ServiceProvider.GetRequiredService<MaterializeSegmentHandler>();
			var processed = await handler.ProcessDueAsync($"evidence-test-{round}", 10, ct);
			if (processed.Count == 0)
			{
				break;
			}

			outcomes.AddRange(processed);
		}

		return outcomes;
	}

	/// <summary>Materializes the segments and requires that all of them became available.</summary>
	public async Task MaterializeAllAsync(CancellationToken ct)
	{
		await MaterializeSegmentsAsync(ct);
		(await ScalarAsync("SELECT count(*) FROM socalytics.materialized_segments WHERE state = 'available'", ct))
			.ShouldBe((long)SegmentCount * Recordings.Count);
	}

	public async Task<long> ScalarAsync(string sql, CancellationToken ct)
	{
		await using var connection = new NpgsqlConnection(Setup.Db.MigratorConnectionString);
		await connection.OpenAsync(ct);
		await using var command = new NpgsqlCommand(sql, connection);
		return Convert.ToInt64(await command.ExecuteScalarAsync(ct));
	}

	public async ValueTask DisposeAsync()
	{
		Viewer.Dispose();
		Outsider.Dispose();
		await Setup.DisposeAsync();
		media.Dispose();
		DeleteDirectory(workingDirectory);
	}

	private static Dictionary<string, string?> Settings(string workingDirectory)
	{
		var dir = new DirectoryInfo(AppContext.BaseDirectory);
		while (!File.Exists(Path.Combine(dir!.FullName, "src", "platform", "SocAlytics.Platform.Api", "appsettings.Development.json")))
		{
			dir = dir.Parent;
		}

		var configuration = new ConfigurationBuilder()
			.AddJsonFile(Path.Combine(dir.FullName, "src", "platform", "SocAlytics.Platform.Api", "appsettings.Development.json"))
			.Build();
		var values = configuration.GetSection("Analysis").AsEnumerable(true).ToDictionary(p => "Analysis:" + p.Key, p => p.Value);
		values["Analysis:DefaultWorkflow:Nodes:0:VideoPolicy"] = "required";
		values["Analysis:DefaultWorkflow:SegmentationPolicy:DurationSeconds"] = PolicySeconds.ToString();
		values["Segments:Toolchain:FfmpegPath"] = SyntheticMedia.FfmpegPath;
		values["Segments:Toolchain:FfprobePath"] = Path.Combine(Path.GetDirectoryName(SyntheticMedia.FfmpegPath) ?? string.Empty, "ffprobe");
		values["Segments:Materialization:WorkingDirectory"] = workingDirectory;
		values["Segments:Materialization:ExecutionTimeout"] = "00:01:00";
		values["Segments:Materialization:MaxSegmentBytes"] = (8 * 1024 * 1024).ToString();
		return values;
	}

	private static async Task<ApiSession> MemberAsync(FinalizationSetup setup, string name, string? teamRole, CancellationToken ct)
	{
		var id = await TestMembers.SeedAsync(setup.Db, name, cancellationToken: ct);
		if (teamRole is not null)
		{
			await using var connection = new NpgsqlConnection(setup.Db.MigratorConnectionString);
			await connection.OpenAsync(ct);
			await connection.ExecuteAsync(new CommandDefinition(
				"INSERT INTO socalytics.team_role_assignment (member_account_id, team_id, role, assigned_at, assigned_by_account_id) VALUES (@id, @team, @role, now(), @id)",
				new { id, team = setup.Hierarchy.TeamId, role = teamRole }, cancellationToken: ct));
		}

		return await ApiSession.SignInAsync(setup.Factory, name, TestMembers.DefaultPassword, ct);
	}

	private static async Task<Guid> FinalizeSetAsync(
		ApiSession session, Guid matchId, IReadOnlyList<UploadedSyntheticRecording> recordings, CancellationToken ct)
	{
		var members = recordings.Select(r => new { recordingVersionId = r.RecordingVersionId, timelineMappingId = r.TimelineMappingId }).ToArray();
		using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/matches/{matchId}/recording-set-versions")
		{
			Content = JsonContent.Create(new { members }),
		};
		request.Headers.Add("X-CSRF-Token", session.AntiforgeryToken);
		request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
		using var response = await session.Client.SendAsync(request, ct);
		var text = await response.Content.ReadAsStringAsync(ct);
		response.StatusCode.ShouldBe(HttpStatusCode.Created, text);
		return JsonDocument.Parse(text).RootElement.GetProperty("id").GetGuid();
	}

	private static async Task<Guid> CreateRunAsync(FinalizationSetup setup, Guid recordingSetVersionId, CancellationToken ct)
	{
		await using var scope = setup.Factory.Services.CreateAsyncScope();
		var lineage = (await scope.ServiceProvider.GetRequiredService<IRecordingSetLookup>().GetAsync(recordingSetVersionId, ct))!;
		var notification = new RecordingsFinalizedNotification(
			Guid.NewGuid(), "1.0.0", DateTimeOffset.UtcNow, lineage.RecordingSetVersionId, lineage.MatchId, lineage.TeamId,
			[.. lineage.Members.Select(m => new FinalizedNotificationMember(
				m.Position, m.RecordingVersionId, m.RecordingContentDigest, m.TimelineMappingId, m.TimelineMappingDigest))]);
		var outcome = await scope.ServiceProvider.GetRequiredService<HandleRecordingsFinalizedNotificationHandler>().HandleAsync(notification, ct);
		return outcome.ShouldBeOfType<NotificationOutcome.RunCreated>().RunId;
	}

	// Accepts a result of the first segment job and indexes the inside fact and the boundary fact for it.
	private static async Task<Guid> SeedFactsAsync(FinalizationSetup setup, Guid runId, CancellationToken ct)
	{
		await using var connection = new NpgsqlConnection(setup.Db.MigratorConnectionString);
		await connection.OpenAsync(ct);
		var job = await connection.QueryFirstAsync<(Guid Id, Guid NodeId)>(new CommandDefinition(
			"SELECT id AS Id, node_id AS NodeId FROM socalytics.analysis_logical_jobs WHERE run_id = @runId AND segment_number = 1 ORDER BY id LIMIT 1",
			new { runId }, cancellationToken: ct));

		var occurrence = Guid.NewGuid();
		var attempt = Guid.NewGuid();
		await connection.ExecuteAsync(new CommandDefinition(
			"INSERT INTO socalytics.analysis_readiness_occurrences (id, logical_job_id, sequence, cause, created_at) VALUES (@occurrence, @jobId, 1, 'initial', now())",
			new { occurrence, jobId = job.Id }, cancellationToken: ct));
		await connection.ExecuteAsync(new CommandDefinition(
			"""
			INSERT INTO socalytics.analysis_execution_attempts
				(id, logical_job_id, readiness_occurrence_id, attempt_number, manager_id, fencing_token, state, claimed_at, lease_expires_at, last_heartbeat_at)
			VALUES (@attempt, @jobId, @occurrence, 1, gen_random_uuid(), nextval('socalytics.analysis_fencing_token_seq'), 'Stale', now(), now(), now())
			""",
			new { attempt, jobId = job.Id, occurrence }, cancellationToken: ct));

		var result = new AcceptedResultRecord(
			Guid.NewGuid(), job.Id, runId, job.NodeId, attempt, "results/evidence", DigestA, CapabilityId, "1.0.0", "1.0.0",
			DigestB, DigestA, null, "{}", "{}", [], DateTimeOffset.UtcNow);
		IndexedFactRecord Fact(int ordinal, long start, long end) => new(
			result.Id, ordinal, runId, job.NodeId, setup.Hierarchy.TeamId, setup.Hierarchy.MatchId, CapabilityId, "1.0.0", "1.0.0",
			"foundational-facts", "pass", start, end, 0.5, $$"""{"o":{{ordinal}}}""");

		await using var scope = setup.Factory.Services.CreateAsyncScope();
		await using var uow = await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().BeginAsync(ct);
		await scope.ServiceProvider.GetRequiredService<IAcceptedResultStore>().InsertAsync(result, ct);
		await scope.ServiceProvider.GetRequiredService<IIndexedFactStore>().InsertAsync(
			[
				Fact(InsideFactOrdinal, InsideFactStartMs, InsideFactEndMs),
				Fact(BoundaryFactOrdinal, BoundaryFactStartMs, BoundaryFactEndMs),
			],
			ct);
		await uow.CommitAsync(ct);
		return result.Id;
	}

	private static void DeleteDirectory(string path)
	{
		try
		{
			if (Directory.Exists(path))
			{
				Directory.Delete(path, recursive: true);
			}
		}
		catch (IOException)
		{
		}
		catch (UnauthorizedAccessException)
		{
		}
	}
}
