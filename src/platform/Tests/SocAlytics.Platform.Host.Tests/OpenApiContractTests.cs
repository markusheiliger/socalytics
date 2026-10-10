using System.Text.Json;
using Aspire.Hosting;
using Aspire.Hosting.Testing;
using Shouldly;
using Xunit;

namespace SocAlytics.Platform.Host.Tests;

public sealed class OpenApiContractTests
{
	[Fact]
	public async Task OpenApiDocumentMatchesContractOperationsAndSecuritySchemes()
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
		var paths = root.GetProperty("paths");

		var mismatches = new List<string>();
		foreach (var contract in ContractOperations.All)
		{
			paths.TryGetProperty(contract.Path, out var pathItem).ShouldBeTrue($"{contract.OperationId}: path {contract.Path}");
			pathItem.TryGetProperty(contract.Method.ToLowerInvariant(), out var operation)
				.ShouldBeTrue($"{contract.OperationId}: method {contract.Method}");
			operation.GetProperty("operationId").GetString().ShouldBe(contract.OperationId);

			var statusCodes = operation.GetProperty("responses").EnumerateObject().Select(p => p.Name).Order().ToArray();
			if (!statusCodes.SequenceEqual(contract.StatusCodes.Order()))
			{
				mismatches.Add($"{contract.OperationId}: expected [{string.Join(",", contract.StatusCodes.Order())}] but was [{string.Join(",", statusCodes)}]");
			}

			var security = operation.TryGetProperty("security", out var s)
				? s.EnumerateArray().SelectMany(r => r.EnumerateObject().Select(p => p.Name)).Order().ToArray()
				: null;
			if (contract.OperationId is "signIn" or "redeemCredential")
			{
				(security ?? []).ShouldBeEmpty($"{contract.OperationId}: security");
			}
			else if (contract.Method != "GET")
			{
				security.ShouldBe(["antiforgeryHeader", "sessionCookie"], $"{contract.OperationId}: security");
			}
		}

		mismatches.ShouldBeEmpty();

		var operationCount = paths.EnumerateObject().Sum(p => p.Value.EnumerateObject().Count(o => o.Name != "parameters"));
		operationCount.ShouldBe(ContractOperations.All.Count);

		var schemes = root.GetProperty("components").GetProperty("securitySchemes");
		var cookie = schemes.GetProperty("sessionCookie");
		cookie.GetProperty("type").GetString().ShouldBe("apiKey");
		cookie.GetProperty("in").GetString().ShouldBe("cookie");
		cookie.GetProperty("name").GetString().ShouldBe("__Host-socalytics-session");
		var header = schemes.GetProperty("antiforgeryHeader");
		header.GetProperty("type").GetString().ShouldBe("apiKey");
		header.GetProperty("in").GetString().ShouldBe("header");
		header.GetProperty("name").GetString().ShouldBe("X-CSRF-Token");

		root.GetProperty("security").EnumerateArray()
			.Any(r => r.TryGetProperty("sessionCookie", out _)).ShouldBeTrue();
	}
}
