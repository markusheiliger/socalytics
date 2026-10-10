using System.Text.Json;
using Aspire.Hosting;
using Aspire.Hosting.Testing;
using Shouldly;
using Xunit;

namespace SocAlytics.Platform.Host.Tests;

public sealed class RecordingsOpenApiTests
{
	private sealed record Op(string Id, string Path, string Method, string[] Codes, bool Idempotency);

	private static readonly Op[] Operations =
	[
		new("startRecordingUpload", "/api/v1/matches/{matchId}/upload-sessions", "post", ["201", "400", "401", "403", "404", "409", "503"], true),
		new("getRecordingUploadSession", "/api/v1/matches/{matchId}/upload-sessions/{uploadSessionId}", "get", ["200", "401", "403", "404"], false),
		new("issueRecordingUploadGrants", "/api/v1/matches/{matchId}/upload-sessions/{uploadSessionId}/grants", "post", ["200", "400", "401", "403", "404", "409", "503"], false),
		new("completeRecordingUpload", "/api/v1/matches/{matchId}/upload-sessions/{uploadSessionId}/completion", "post", ["201", "400", "401", "403", "404", "409", "503"], true),
		new("getRecordingVersion", "/api/v1/matches/{matchId}/recording-versions/{recordingVersionId}", "get", ["200", "401", "403", "404"], false),
		new("reviseRecordingTimelineMapping", "/api/v1/matches/{matchId}/recording-versions/{recordingVersionId}/timeline-mappings", "post", ["200", "201", "400", "401", "403", "404", "409"], true),
		new("getRecordingTimelineMapping", "/api/v1/matches/{matchId}/timeline-mappings/{timelineMappingId}", "get", ["200", "401", "403", "404"], false),
		new("finalizeRecordingSet", "/api/v1/matches/{matchId}/recording-set-versions", "post", ["201", "400", "401", "403", "404", "409"], true),
		new("getRecordingSetVersion", "/api/v1/matches/{matchId}/recording-set-versions/{recordingSetVersionId}", "get", ["200", "401", "403", "404"], false),
		new("getMatchRecordingLineage", "/api/v1/matches/{matchId}/recording-lineage", "get", ["200", "304", "401", "403", "404"], false),
	];

	[Fact]
	public async Task OpenApiDocumentContainsRecordingOperationsWithoutBinaryBodies()
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

		foreach (var op in Operations)
		{
			paths.TryGetProperty(op.Path, out var item).ShouldBeTrue($"{op.Id}: path");
			item.TryGetProperty(op.Method, out var operation).ShouldBeTrue($"{op.Id}: method");
			operation.GetProperty("operationId").GetString().ShouldBe(op.Id);

			var codes = operation.GetProperty("responses").EnumerateObject().Select(p => p.Name).ToHashSet();
			foreach (var code in op.Codes)
			{
				codes.ShouldContain(code, $"{op.Id}: response {code}");
			}

			var parameters = operation.TryGetProperty("parameters", out var ps)
				? ps.EnumerateArray().Select(p => p.GetProperty("name").GetString()).ToArray()
				: [];
			parameters.Contains("Idempotency-Key").ShouldBe(op.Idempotency, $"{op.Id}: Idempotency-Key");
			parameters.ShouldNotContain("X-CSRF-Token", $"{op.Id}: X-CSRF-Token");

			if (op.Method == "post")
			{
				var security = operation.GetProperty("security").EnumerateArray()
					.SelectMany(r => r.EnumerateObject().Select(p => p.Name)).Order().ToArray();
				security.ShouldBe(["antiforgeryHeader", "sessionCookie"], $"{op.Id}: security");
			}

			if (operation.TryGetProperty("requestBody", out var body))
			{
				foreach (var media in body.GetProperty("content").EnumerateObject())
				{
					media.Name.ShouldNotBe("application/octet-stream", op.Id);
					media.Name.ShouldNotStartWith("multipart/", Case.Insensitive, op.Id);
					if (media.Value.TryGetProperty("schema", out var schema))
					{
						schema.GetRawText().ShouldNotContain("\"binary\"", Case.Insensitive, op.Id);
					}
				}
			}
		}
	}
}
