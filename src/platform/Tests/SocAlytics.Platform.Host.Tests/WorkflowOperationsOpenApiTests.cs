using System.Text.Json;
using Aspire.Hosting;
using Aspire.Hosting.Testing;
using Shouldly;
using Xunit;

namespace SocAlytics.Platform.Host.Tests;

public sealed class WorkflowOperationsOpenApiTests
{
	private sealed record Op(string Id, string Path, string Method, string Schema, string[] Query);

	private static readonly Op[] Operations =
	[
		new("getAnalysisOperationalSummary", "/api/v1/operations/analysis/summary", "get", "AnalysisOperationalSummaryDto", []),
		new("listOutboxMessages", "/api/v1/operations/outbox-messages", "get", "OutboxMessagePageDto", ["state", "messageType", "pageSize", "continuationToken"]),
		new("getOutboxMessage", "/api/v1/operations/outbox-messages/{messageId}", "get", "OutboxMessageDto", []),
		new("listNotificationReceipts", "/api/v1/operations/analysis/notification-receipts", "get", "NotificationReceiptPageDto", ["outcome", "pageSize", "continuationToken"]),
		new("reconcileAnalysisPublication", "/api/v1/operations/analysis/reconciliations", "post", "ReconciliationResultDto", []),
	];

	[Fact]
	public async Task OpenApiDocumentTypesTheWorkflowOperations()
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
		var paths = document.RootElement.GetProperty("paths");
		var schemas = document.RootElement.GetProperty("components").GetProperty("schemas");

		foreach (var op in Operations)
		{
			paths.TryGetProperty(op.Path, out var item).ShouldBeTrue($"{op.Id}: path");
			item.TryGetProperty(op.Method, out var operation).ShouldBeTrue($"{op.Id}: method");
			operation.GetProperty("operationId").GetString().ShouldBe(op.Id);

			var schema = operation.GetProperty("responses").GetProperty("200")
				.GetProperty("content").GetProperty("application/json").GetProperty("schema");
			schema.GetProperty("$ref").GetString().ShouldBe($"#/components/schemas/{op.Schema}", $"{op.Id}: 200 schema");
			schemas.TryGetProperty(op.Schema, out _).ShouldBeTrue($"{op.Id}: component {op.Schema}");

			var query = operation.TryGetProperty("parameters", out var ps)
				? ps.EnumerateArray()
					.Where(p => p.GetProperty("in").GetString() == "query")
					.Select(p => p.GetProperty("name").GetString()).ToArray()
				: [];
			foreach (var name in op.Query)
			{
				query.ShouldContain(name, $"{op.Id}: query parameter {name}");
			}
		}
	}
}
