using System.Text.Json;
using Aspire.Hosting;
using Aspire.Hosting.Testing;
using Shouldly;
using Xunit;

namespace SocAlytics.Platform.Host.Tests;

public sealed class RegistryOpenApiTests
{
	// operationIds of specs/20261005-130704-analyst-manager-registration/contracts/openapi.yaml
	private static readonly string[] OperationIds =
	[
		"getAnalystManagerStampDiscovery",
		"createAnalystManagerPairing",
		"getAnalystManagerPairingStatus",
		"activateAnalystManagerRegistration",
		"issueAnalystManagerAccessToken",
		"getOwnAnalystManagerRegistration",
		"beginAnalystManagerUnregistration",
		"completeAnalystManagerUnregistration",
		"submitAnalystManagerPairingCode",
		"listAnalystManagerRegistrations",
		"getAnalystManagerRegistration",
		"approveAnalystManagerRegistration",
		"rejectAnalystManagerRegistration",
		"revokeAnalystManagerRegistration",
	];

	[Fact]
	public async Task OpenApiDocumentContainsAllRegistryOperationIds()
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

		OperationIds.Length.ShouldBe(14);
		foreach (var id in OperationIds)
		{
			ids.ShouldContain(id);
		}
	}
}
