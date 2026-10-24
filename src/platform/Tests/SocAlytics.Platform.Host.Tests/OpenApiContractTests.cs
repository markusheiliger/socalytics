using System.Text.Json;
using Aspire.Hosting;
using Aspire.Hosting.Testing;
using Shouldly;
using Xunit;

namespace SocAlytics.Platform.Host.Tests;

public sealed class OpenApiContractTests
{
	[Fact]
	// Quickstart A40
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

		// Recording operations are verified by RecordingsOpenApiTests and Registry operations by
		// RegistryOpenApiTests and AnalystRegistryOpenApiTests, not against the Club contract.
		var operationCount = paths.EnumerateObject()
			.Where(p => !IsRecordingPath(p.Name) && !IsRegistryPath(p.Name) && !IsAnalysisPath(p.Name) && !IsEmailPath(p.Name) && !IsExternalSignInPath(p.Name) && !IsPasswordRecoveryPath(p.Name) && !IsInvitationPath(p.Name) && !IsAgentOrchestrationPath(p.Name) && !IsMaterializedSegmentPath(p.Name))
			.Sum(p => p.Value.EnumerateObject().Count(o => o.Name != "parameters"));
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

	// Analysis operations are verified against specs/archive/20261005-130703-durable-analysis-workflow/contracts/openapi.yaml
	// by the Analysis tests and T039, not against the Club contract.
	// Email operations are verified by the Notifications tests, not against the Club contract.
	private static bool IsEmailPath(string path) =>
		path == "/api/v1/me/email"
		|| path.StartsWith("/api/v1/me/email/", StringComparison.Ordinal)
		|| path == "/api/v1/email-confirmations"
		|| path == "/api/v1/me/notification-preferences"
		|| path.StartsWith("/api/v1/me/notification-preferences/", StringComparison.Ordinal)
		|| path == "/api/v1/members/{memberId}/email";

	// Agent orchestration operations are verified by the Agent Orchestration tests, not against the Club contract.
	private static bool IsAgentOrchestrationPath(string path) =>
		path.StartsWith("/api/v1/agent-", StringComparison.Ordinal)
		|| path.EndsWith("/agent-conversations", StringComparison.Ordinal);

	// External sign-in operations are verified by ExternalSignInEndpointTests against
	// specs/archive/20261015-145134-oidc-sign-in/contracts/external-sign-in-api.md, not against the Club contract.
	private static bool IsExternalSignInPath(string path) =>
		path == "/api/v1/identity-providers"
		|| path == "/api/v1/session/external"
		|| path == "/api/v1/session/external/callback"
		|| (path.StartsWith("/api/v1/members/", StringComparison.Ordinal)
			&& path.Contains("/external-identities/", StringComparison.Ordinal));

	// Password recovery operations are verified against
	// specs/archive/20261017-200823-password-recovery/contracts/password-recovery-api.md, not against the Club contract.
	private static bool IsPasswordRecoveryPath(string path) =>
		path.StartsWith("/api/v1/password-recovery/", StringComparison.Ordinal);

	// Invitation operations are verified against
	// specs/archive/20261017-202624-member-invitations/contracts/member-invitations-api.md, not against the Club contract.
	private static bool IsInvitationPath(string path) =>
		path == "/api/v1/invitations"
		|| path.StartsWith("/api/v1/invitations/", StringComparison.Ordinal);

	// Materialized segment operations are verified by MaterializedSegmentEndpointTests against
	// specs/archive/20261017-222433-materialized-segments/contracts/member-operations.md, not against the Club contract.
	private static bool IsMaterializedSegmentPath(string path) =>
		path.StartsWith("/api/v1/matches/", StringComparison.Ordinal)
		&& (path.EndsWith("/materialized-segments", StringComparison.Ordinal)
			|| path.EndsWith("/materialized-segments/purge", StringComparison.Ordinal));

	private static bool IsAnalysisPath(string path) =>
		path.StartsWith("/api/v1/analysis/", StringComparison.Ordinal)
		|| path.StartsWith("/api/v1/operations/", StringComparison.Ordinal)
		|| path.EndsWith("/analysis-runs", StringComparison.Ordinal);

	private static bool IsRecordingPath(string path)
	{
		const string prefix = "/api/v1/matches/{matchId}/";
		if (!path.StartsWith(prefix, StringComparison.Ordinal))
		{
			return false;
		}

		var rest = path[prefix.Length..];
		foreach (var segment in new[] { "upload-sessions", "recording-versions", "timeline-mappings", "recording-set-versions" })
		{
			if (rest == segment || rest.StartsWith(segment + "/", StringComparison.Ordinal))
			{
				return true;
			}
		}

		return rest == "recording-lineage";
	}

	private static bool IsRegistryPath(string path)
	{
		foreach (var prefix in new[] { "/api/v1/analyst-managers", "/api/v1/analyst-manager-registrations", "/api/v1/registry" })
		{
			if (path == prefix || path.StartsWith(prefix + "/", StringComparison.Ordinal))
			{
				return true;
			}
		}

		return false;
	}
}
