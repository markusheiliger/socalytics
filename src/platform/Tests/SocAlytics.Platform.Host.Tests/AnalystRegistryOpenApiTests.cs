using System.Text.Json;
using Aspire.Hosting;
using Aspire.Hosting.Testing;
using Shouldly;
using Xunit;

namespace SocAlytics.Platform.Host.Tests;

public sealed class AnalystRegistryOpenApiTests
{
	// operationIds of the Analyst registry contract (specs/20261012-152148-analyst-registry/contracts/registry-api.md)
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
}
