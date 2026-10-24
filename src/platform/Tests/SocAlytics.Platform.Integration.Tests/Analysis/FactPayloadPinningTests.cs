using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using SocAlytics.Platform.Application.Analysis;
using SocAlytics.Platform.Application.Analysis.Commands;
using SocAlytics.Platform.Application.Recordings;
using SocAlytics.Platform.Application.Registry;
using SocAlytics.Platform.Domain.Registry;
using SocAlytics.Platform.Integration.Tests.Infrastructure;
using SocAlytics.Platform.Integration.Tests.Recordings;
using SocAlytics.Platform.Integration.Tests.Recordings.Support;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.Analysis;

public sealed class FactPayloadPinningTests(PostgresContainerFixture postgres, RustFsContainerFixture rustFs)
{
	private const string FirstSchema = """{"type":"object","properties":{"second":{"type":"integer","minimum":0}},"required":["second"],"additionalProperties":false}""";
	private const string ChangedSchema = """{"type":"object","properties":{"second":{"type":"string"}},"required":["second"]}""";

	private sealed class SchemaResolver(IWorkflowDefinitionResolver inner, SchemaResolver.Setting setting) : IWorkflowDefinitionResolver
	{
		public sealed class Setting
		{
			public volatile IReadOnlyDictionary<string, JsonElement>? Schemas;
		}

		public async Task<WorkflowResolution> ResolveDefaultAsync(CancellationToken ct)
		{
			var resolution = await inner.ResolveDefaultAsync(ct);
			if (resolution is not WorkflowResolution.ResolvedResult resolved || setting.Schemas is not { } schemas)
			{
				return resolution;
			}

			var definition = resolved.Definition;
			var nodes = definition.Nodes.Select((n, i) => i != 0 ? n : n with
			{
				FactPayloadSchemas = schemas,
				FactPayloadSchemasDigest = FactPayloadSchemaLimits.Digest(schemas),
			}).ToList();
			return WorkflowResolution.Resolved(definition with { Nodes = nodes });
		}
	}

	private static IReadOnlyDictionary<string, JsonElement> Schemas(string schema) =>
		new Dictionary<string, JsonElement> { ["frame-second-probe"] = JsonDocument.Parse(schema).RootElement.Clone() };

	// jsonb normalizes whitespace, so the stored schema is compared structurally.
	private static void AssertSameSchema(JsonElement actual, IReadOnlyDictionary<string, JsonElement> schemas) =>
		System.Text.Json.Nodes.JsonNode.DeepEquals(
			System.Text.Json.Nodes.JsonNode.Parse(actual.GetRawText()),
			System.Text.Json.Nodes.JsonNode.Parse(schemas["frame-second-probe"].GetRawText())).ShouldBeTrue();

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
		SchemaResolver.Setting setting, CancellationToken ct)
	{
		var setup = await FinalizationSetup.StartAsync(postgres, rustFs, ct, sets: null, configureServices: services =>
		{
			var original = services.Last(d => d.ServiceType == typeof(IWorkflowDefinitionResolver));
			services.Remove(original);
			services.AddSingleton<IWorkflowDefinitionResolver>(sp => new SchemaResolver(
				(IWorkflowDefinitionResolver)ActivatorUtilities.CreateInstance(sp, original.ImplementationType!), setting));
		}, extraSettings: DevWorkflow());
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

	private static async Task<List<JsonDocument>> NodeSnapshotsAsync(FinalizationSetup s, CancellationToken ct)
	{
		await using var connection = new NpgsqlConnection(s.Db.MigratorConnectionString);
		await connection.OpenAsync(ct);
		await using var command = new NpgsqlCommand(
			"""
			SELECT ns.snapshot::text FROM socalytics.analysis_node_snapshots ns
			JOIN socalytics.analysis_workflow_nodes n ON n.id = ns.node_id ORDER BY n.ordinal
			""", connection);
		await using var reader = await command.ExecuteReaderAsync(ct);
		var result = new List<JsonDocument>();
		while (await reader.ReadAsync(ct))
		{
			result.Add(JsonDocument.Parse(reader.GetString(0)));
		}

		return result;
	}

	private static async Task<string> RunDigestAsync(FinalizationSetup s, CancellationToken ct)
	{
		await using var connection = new NpgsqlConnection(s.Db.MigratorConnectionString);
		await connection.OpenAsync(ct);
		await using var command = new NpgsqlCommand("SELECT snapshot_digest FROM socalytics.analysis_run_snapshots", connection);
		return (string)(await command.ExecuteScalarAsync(ct))!;
	}

	[Fact]
	public async Task NodeSnapshotCarriesTheSchemasAndTheirDigest()
	{
		var ct = TestContext.Current.CancellationToken;
		var schemas = Schemas(FirstSchema);
		var (n, s) = await StartAsync(new SchemaResolver.Setting { Schemas = schemas }, ct);
		await using var setupScope = s;
		(await HandleAsync(s, n, ct)).ShouldBeOfType<NotificationOutcome.RunCreated>();

		var nodes = await NodeSnapshotsAsync(s, ct);
		nodes.Count.ShouldBe(3);
		var pinned = nodes[0].RootElement;
		AssertSameSchema(pinned.GetProperty("factPayloadSchemas").GetProperty("frame-second-probe"), schemas);
		pinned.GetProperty("factPayloadSchemasDigest").GetString().ShouldBe(FactPayloadSchemaLimits.Digest(schemas));
		foreach (var other in nodes.Skip(1))
		{
			other.RootElement.TryGetProperty("factPayloadSchemas", out _).ShouldBeFalse();
			other.RootElement.TryGetProperty("factPayloadSchemasDigest", out _).ShouldBeFalse();
		}
	}

	[Fact]
	public async Task LaterRegistrationWithAChangedSchemaDoesNotChangeAnExistingRun()
	{
		var ct = TestContext.Current.CancellationToken;
		var schemas = Schemas(FirstSchema);
		var setting = new SchemaResolver.Setting { Schemas = schemas };
		var (n, s) = await StartAsync(setting, ct);
		await using var setupScope = s;
		(await HandleAsync(s, n, ct)).ShouldBeOfType<NotificationOutcome.RunCreated>();
		var digest = await RunDigestAsync(s, ct);

		setting.Schemas = Schemas(ChangedSchema);
		(await HandleAsync(s, n with { EventId = Guid.NewGuid() }, ct)).ShouldBeOfType<NotificationOutcome.RunExists>();

		(await s.ScalarAsync("SELECT count(*) FROM socalytics.analysis_runs", ct)).ShouldBe(1);
		(await RunDigestAsync(s, ct)).ShouldBe(digest);
		var pinned = (await NodeSnapshotsAsync(s, ct))[0].RootElement;
		AssertSameSchema(pinned.GetProperty("factPayloadSchemas").GetProperty("frame-second-probe"), schemas);
		pinned.GetProperty("factPayloadSchemasDigest").GetString().ShouldBe(FactPayloadSchemaLimits.Digest(schemas));
	}

	[Fact]
	public async Task DeclarationWithoutSchemasLeavesTheSnapshotAndRunDigestUnchanged()
	{
		var ct = TestContext.Current.CancellationToken;
		var (n, s) = await StartAsync(new SchemaResolver.Setting(), ct);
		await using var setupScope = s;
		(await HandleAsync(s, n, ct)).ShouldBeOfType<NotificationOutcome.RunCreated>();

		foreach (var node in await NodeSnapshotsAsync(s, ct))
		{
			node.RootElement.GetRawText().ShouldNotContain("factPayloadSchemas");
		}

		await using var scope = s.Factory.Services.CreateAsyncScope();
		var definition = ((WorkflowResolution.ResolvedResult)await scope.ServiceProvider.GetRequiredService<IWorkflowDefinitionResolver>().ResolveDefaultAsync(ct)).Definition;
		var mapper = scope.ServiceProvider.GetRequiredService<WorkflowSnapshotMapper>();
		var without = mapper.Map(definition);
		without.RunSnapshotJson.ShouldNotContain("factPayloadSchemas");
		(await RunDigestAsync(s, ct)).ShouldBe(without.RunSnapshotDigest);

		var schemas = Schemas(FirstSchema);
		var with = mapper.Map(definition with
		{
			Nodes = definition.Nodes.Select((x, i) => i != 0 ? x : x with
			{
				FactPayloadSchemas = schemas,
				FactPayloadSchemasDigest = FactPayloadSchemaLimits.Digest(schemas),
			}).ToList(),
		});
		with.RunSnapshotDigest.ShouldNotBe(without.RunSnapshotDigest);
	}
}
