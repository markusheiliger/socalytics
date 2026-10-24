using System.Text.Json;
using Aspire.Hosting;
using Aspire.Hosting.Testing;
using Shouldly;
using Xunit;

namespace SocAlytics.Platform.Host.Tests;

public sealed class AgentCatalogOpenApiTests
{
	private sealed record Op(string Id, string Method, string[] SuccessCodes, bool HasBody);

	// specs/archive/20261018-103316-web-ui-agent-catalog/contracts/agent-catalog-openapi.md
	private static readonly Op[] Operations =
	[
		new("getAgentCatalogBaseline", "get", ["200"], false),
		new("registerAgentCatalogArtifact", "post", ["200", "201"], true),
		new("listAgentCatalogArtifacts", "get", ["200"], false),
		new("getAgentCatalogArtifact", "get", ["200"], false),
		new("registerAgentVersion", "post", ["200", "201"], true),
		new("listAgentVersions", "get", ["200"], false),
		new("getAgentVersion", "get", ["200"], false),
		new("approveAgentVersion", "post", ["200"], true),
		new("retireAgentVersion", "post", ["200"], false),
		new("listAgentEnablements", "get", ["200"], false),
		new("setAgentEnablement", "put", ["200"], true),
		new("listAvailableAgents", "get", ["200"], false),
		new("listMembers", "get", ["200"], false),
	];

	private static readonly string[] Kinds =
	[
		"dimension-set", "philosophy-profile", "prompt-template", "tool-policy", "safety-policy", "model-selection",
		"modifier-policy",
	];

	[Fact]
	public async Task OpenApiDocumentDescribesCatalogBodiesAndDocumentSchemas()
	{
		using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
		var appHost = await DistributedApplicationTestingBuilder.CreateAsync<Projects.SocAlytics_Platform_AppHost>(
			["--SocAlytics:LocalDatabase:Persistent=false", "--SocAlytics:ApiHttpsEndpoint=false"], timeout.Token);
		await using var app = await appHost.BuildAsync(timeout.Token);
		await app.StartAsync(timeout.Token);
		await app.ResourceNotifications.WaitForResourceHealthyAsync("api", timeout.Token);

		using var client = app.CreateHttpClient("api", "http");
		var json = await client.GetStringAsync("/openapi/v1.json", timeout.Token);
		using var document = JsonDocument.Parse(json);
		var root = document.RootElement;
		var schemas = root.GetProperty("components").GetProperty("schemas");

		var operations = root.GetProperty("paths").EnumerateObject()
			.SelectMany(p => p.Value.EnumerateObject()
				.Where(o => o.Value.ValueKind == JsonValueKind.Object && o.Value.TryGetProperty("operationId", out _))
				.Select(o => (Method: o.Name, Operation: o.Value)))
			.ToDictionary(o => o.Operation.GetProperty("operationId").GetString()!);

		foreach (var op in Operations)
		{
			operations.TryGetValue(op.Id, out var found).ShouldBeTrue($"{op.Id}: operation");
			found.Method.ShouldBe(op.Method, $"{op.Id}: method");
			var operation = found.Operation;

			var responses = operation.GetProperty("responses");
			foreach (var code in op.SuccessCodes)
			{
				responses.TryGetProperty(code, out var response).ShouldBeTrue($"{op.Id}: response {code}");
				response.GetProperty("content").TryGetProperty("application/json", out var media)
					.ShouldBeTrue($"{op.Id}: {code} application/json");
				media.TryGetProperty("schema", out _).ShouldBeTrue($"{op.Id}: {code} schema");
			}

			if (op.HasBody)
			{
				operation.TryGetProperty("requestBody", out var body).ShouldBeTrue($"{op.Id}: requestBody");
				body.GetProperty("content").TryGetProperty("application/json", out var media)
					.ShouldBeTrue($"{op.Id}: request application/json");
				media.TryGetProperty("schema", out _).ShouldBeTrue($"{op.Id}: request schema");
			}
		}

		var recordDocument = Resolve(schemas, schemas.GetProperty("AgentCatalogRecordDocument"));
		recordDocument.GetProperty("discriminator").GetProperty("propertyName").GetString().ShouldBe("kind");
		var mapping = recordDocument.GetProperty("discriminator").GetProperty("mapping");
		mapping.EnumerateObject().Select(m => m.Name).Order().ShouldBe(Kinds.Order());

		foreach (var kind in Kinds)
		{
			var reference = mapping.GetProperty(kind).GetString()!;
			var schema = schemas.GetProperty(reference[(reference.LastIndexOf('/') + 1)..]);
			PropertyNames(schema).ShouldBe(ContractPropertyNames(kind), $"{kind}: document properties");
		}

		PropertyNames(schemas.GetProperty("AgentVersionDocument")).ShouldBe(
			ContractPropertyNames("agent-version"), "agent-version: document properties");
	}

	private static JsonElement Resolve(JsonElement schemas, JsonElement schema)
	{
		while (schema.TryGetProperty("$ref", out var reference))
		{
			var value = reference.GetString()!;
			schema = schemas.GetProperty(value[(value.LastIndexOf('/') + 1)..]);
		}

		return schema;
	}

	private static string[] PropertyNames(JsonElement schema) =>
		schema.GetProperty("properties").EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToArray();

	private static string[] ContractPropertyNames(string kind)
	{
		var path = Path.Combine(FindRepositoryRoot(), "contracts", "agent-catalog", kind, "v1", $"{kind}.schema.json");
		using var contract = JsonDocument.Parse(File.ReadAllText(path));
		return PropertyNames(contract.RootElement);
	}

	private static string FindRepositoryRoot()
	{
		for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
		{
			if (Directory.Exists(Path.Combine(directory.FullName, "src", "platform", "SocAlytics.Platform.Api")))
			{
				return directory.FullName;
			}
		}

		throw new InvalidOperationException("The repository root was not found above the test output directory.");
	}
}
