using System.Text.Json;
using Aspire.Hosting;
using Aspire.Hosting.Testing;
using Shouldly;
using Xunit;

namespace SocAlytics.Platform.Host.Tests;

public sealed class RegistrationContractShapeTests
{
	// Property names of the registration objects the operations returned before they were typed, plus the fleet
	// read fields of specs/archive/20261012-153308-manager-fleet-status/contracts/fleet-read-api.md.
	private static readonly string[] RegistrationProperties =
	[
		"registrationId", "managerId", "stampId", "state", "keyThumbprint", "deviceFingerprint", "deviceMetadata",
		"pairingOrigin", "approvalNotice", "submittedBy", "submittedAt", "decidedBy", "decidedAt", "decisionReason",
		"activatedAt", "endedAt", "endReason", "revokedBy", "pairingExpiresAt", "approvalExpiresAt",
		"activationExpiresAt", "createdAt", "version", "status", "connectivity",
	];

	private static readonly string[] DeviceMetadataProperties =
		["os", "osVersion", "architecture", "managerVersion", "claimedKeyProtection", "claimedApplicationProfile", "hostLabel"];

	private static readonly string[] PairingOriginProperties = ["sourceAddress", "requestedAt"];

	private static readonly string[] StatusProperties =
	[
		"operatingState", "pausedUntil", "runtimeUnavailableReason", "activeAnalysts", "maxAnalysts", "managerVersion",
		"reportedAt",
	];

	private static readonly string[] PageProperties = ["items", "continuationToken"];

	private static readonly (string Path, string Method, string Success, string[] Properties)[] Operations =
	[
		("/api/v1/analyst-manager-registrations", "post", "201", RegistrationProperties),
		("/api/v1/analyst-manager-registrations", "get", "200", PageProperties),
		("/api/v1/analyst-manager-registrations/{registrationId}", "get", "200", RegistrationProperties),
		("/api/v1/analyst-manager-registrations/{registrationId}/approval", "post", "200", RegistrationProperties),
		("/api/v1/analyst-manager-registrations/{registrationId}/rejection", "post", "200", RegistrationProperties),
		("/api/v1/analyst-manager-registrations/{registrationId}/revocation", "post", "200", RegistrationProperties),
	];

	private static readonly (string Path, string Method, string[] Codes)[] ErrorCodes =
	[
		("/api/v1/analyst-manager-registrations|post", "", ["400", "401", "403", "429"]),
		("/api/v1/analyst-manager-registrations|get", "", ["400", "401", "403"]),
		("/api/v1/analyst-manager-registrations/{registrationId}|get", "", ["401", "403", "404"]),
		("/api/v1/analyst-manager-registrations/{registrationId}/approval|post", "", ["401", "403", "404", "409"]),
		("/api/v1/analyst-manager-registrations/{registrationId}/rejection|post", "", ["400", "401", "403", "404", "409"]),
		("/api/v1/analyst-manager-registrations/{registrationId}/revocation|post", "", ["400", "401", "403", "404", "409"]),
	];

	[Fact]
	public async Task RegistrationOperationsDeclareTheExpectedResponseShapes()
	{
		using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
		var appHost = await DistributedApplicationTestingBuilder.CreateAsync<Projects.SocAlytics_Platform_AppHost>(
			["--SocAlytics:LocalDatabase:Persistent=false", "--SocAlytics:ApiHttpsEndpoint=false"], timeout.Token);
		await using var app = await appHost.BuildAsync(timeout.Token);
		await app.StartAsync(timeout.Token);
		await app.ResourceNotifications.WaitForResourceHealthyAsync("api", timeout.Token);

		using var client = app.CreateHttpClient("api", "http");
		using var document = JsonDocument.Parse(await client.GetStringAsync("/openapi/v1.json", timeout.Token));
		var root = document.RootElement;

		foreach (var (path, method, success, expected) in Operations)
		{
			var responses = root.GetProperty("paths").GetProperty(path).GetProperty(method).GetProperty("responses");
			var schema = responses.GetProperty(success).GetProperty("content").GetProperty("application/json").GetProperty("schema");
			PropertyNames(root, schema).ShouldBe(expected, ignoreOrder: true, $"{method} {path}");
		}

		foreach (var (key, _, codes) in ErrorCodes)
		{
			var parts = key.Split('|');
			var responses = root.GetProperty("paths").GetProperty(parts[0]).GetProperty(parts[1]).GetProperty("responses");
			foreach (var code in codes)
			{
				responses.TryGetProperty(code, out _).ShouldBeTrue($"{parts[1]} {parts[0]} declares {code}");
			}
		}

		var registration = Resolve(root, root.GetProperty("paths").GetProperty("/api/v1/analyst-manager-registrations/{registrationId}")
			.GetProperty("get").GetProperty("responses").GetProperty("200").GetProperty("content")
			.GetProperty("application/json").GetProperty("schema"));
		var properties = registration.GetProperty("properties");
		PropertyNames(root, properties.GetProperty("deviceMetadata")).ShouldBe(DeviceMetadataProperties, ignoreOrder: true);
		PropertyNames(root, properties.GetProperty("pairingOrigin")).ShouldBe(PairingOriginProperties, ignoreOrder: true);
		PropertyNames(root, properties.GetProperty("status")).ShouldBe(StatusProperties, ignoreOrder: true);
	}

	private static string[] PropertyNames(JsonElement root, JsonElement schema)
	{
		var resolved = Resolve(root, schema);
		return [.. resolved.GetProperty("properties").EnumerateObject().Select(p => p.Name)];
	}

	private static JsonElement Resolve(JsonElement root, JsonElement schema)
	{
		if (schema.TryGetProperty("oneOf", out var oneOf))
		{
			schema = oneOf.EnumerateArray().First(e => e.TryGetProperty("$ref", out _));
		}

		if (!schema.TryGetProperty("$ref", out var reference))
		{
			return schema;
		}

		var name = reference.GetString()!.Split('/')[^1];
		return root.GetProperty("components").GetProperty("schemas").GetProperty(name);
	}
}
