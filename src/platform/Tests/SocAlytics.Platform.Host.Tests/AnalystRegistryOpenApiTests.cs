using System.Text.Json;
using Aspire.Hosting;
using Aspire.Hosting.Testing;
using Shouldly;
using Xunit;

namespace SocAlytics.Platform.Host.Tests;

public sealed class AnalystRegistryOpenApiTests
{
	// operationIds of the Analyst registry contract (specs/archive/20261012-152148-analyst-registry/contracts/registry-api.md)
	private static readonly string[] OperationIds =
	[
		"registerAnalystImage",
		"listAnalystImages",
		"getAnalystImage",
		"createAnalystProfile",
		"listAnalystProfiles",
		"getAnalystProfile",
		"approveAnalystProfile",
		"retireAnalystProfile",
		"defineWorkflow",
		"listWorkflows",
		"getWorkflow",
		"getDefaultWorkflow",
		"setDefaultWorkflow",
	];

	[Fact]
	public async Task OpenApiDocumentContainsAllAnalystRegistryOperationIds()
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
		var ids = document.RootElement.GetProperty("paths").EnumerateObject()
			.SelectMany(p => p.Value.EnumerateObject().Where(o => o.Value.ValueKind == JsonValueKind.Object))
			.Select(o => o.Value.TryGetProperty("operationId", out var id) ? id.GetString() : null)
			.ToHashSet();

		foreach (var id in OperationIds)
		{
			ids.ShouldContain(id);
		}
	}

	private static readonly string[] OperationIdsWithJsonRequestBody =
	[
		"registerAnalystImage",
		"createAnalystProfile",
		"defineWorkflow",
		"setDefaultWorkflow",
	];

	// specs/archive/20261017-185438-web-ui-registry-admin/contracts/registry-openapi.md
	[Fact]
	public async Task AnalystRegistryOperationsDeclareJsonSchemasForSuccessResponsesAndRequestBodies()
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
		var operations = document.RootElement.GetProperty("paths").EnumerateObject()
			.SelectMany(p => p.Value.EnumerateObject().Where(o => o.Value.ValueKind == JsonValueKind.Object))
			.Where(o => o.Value.TryGetProperty("operationId", out _))
			.ToDictionary(o => o.Value.GetProperty("operationId").GetString()!, o => o.Value);

		foreach (var id in OperationIds)
		{
			operations.ShouldContainKey(id);
			var operation = operations[id];

			var successStatuses = operation.GetProperty("responses").EnumerateObject()
				.Where(r => r.Name.StartsWith('2'))
				.ToList();
			successStatuses.ShouldNotBeEmpty($"{id} declares no success response");
			foreach (var response in successStatuses)
			{
				HasJsonSchema(response.Value).ShouldBeTrue($"{id} {response.Name} declares no application/json response schema");
			}

			if (OperationIdsWithJsonRequestBody.Contains(id))
			{
				operation.TryGetProperty("requestBody", out var body).ShouldBeTrue($"{id} declares no request body");
				HasJsonSchema(body).ShouldBeTrue($"{id} declares no application/json request schema");
			}
		}
	}

	private static bool HasJsonSchema(JsonElement carrier) =>
		carrier.TryGetProperty("content", out var content)
		&& content.TryGetProperty("application/json", out var media)
		&& media.TryGetProperty("schema", out var schema)
		&& schema.ValueKind == JsonValueKind.Object
		&& schema.EnumerateObject().Any();
}
