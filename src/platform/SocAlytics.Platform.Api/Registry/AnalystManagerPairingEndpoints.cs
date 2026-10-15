using System.Text.Json;
using Microsoft.Extensions.Options;
using SocAlytics.Platform.Api.Http;
using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.Registry;
using SocAlytics.Platform.Application.Registry.Pairing;

namespace SocAlytics.Platform.Api.Registry;

internal sealed record DeviceMetadataBody(
	string? Os, string? OsVersion, string? Architecture, string? ManagerVersion, string? ClaimedKeyProtection, string? ClaimedApplicationProfile, string? HostLabel);

internal sealed record PairingRequestBody(string? StampId, JsonElement PublicKey, string? Nonce, DeviceMetadataBody? DeviceMetadata);

internal sealed record PollingHandleBody(string? PollingHandle);

internal sealed record ActivationBody(string? PollingHandle, string? Proof);

internal static class AnalystManagerPairingEndpoints
{
	public static IEndpointRouteBuilder MapAnalystManagerPairingEndpoints(this IEndpointRouteBuilder routes)
	{
		var group = routes.MapGroup("/api/v1/analyst-managers")
			.AllowAnonymous()
			.AddEndpointFilter<RequireHttpsEndpointFilter>()
			.WithTags("Analyst Manager pairing");

		group.MapGet("/stamp", (IOptions<StampOptions> stamp, IOptions<RegistryOptions> registry) =>
			{
				var s = stamp.Value;
				return Results.Json(new
				{
					stampId = s.Id,
					softwareKeysAllowed = registry.Value.AllowSoftwareKeys,
					pairingEndpoint = s.PairingUri,
					activationEndpoint = s.ActivationUri,
					tokenEndpoint = s.TokenUri,
				});
			})
			.RequireRateLimiting(RegistryRateLimiting.Status)
			.WithName("getAnalystManagerStampDiscovery");

		group.MapPost("/pairings", CreatePairingAsync)
			.AddEndpointFilter<JsonOnlyFilter>()
			.RequireRateLimiting(RegistryRateLimiting.PairingCreation)
			.WithName("createAnalystManagerPairing");

		group.MapPost("/pairings/status", GetStatusAsync)
			.AddEndpointFilter<JsonOnlyFilter>()
			.RequireRateLimiting(RegistryRateLimiting.Status)
			.WithName("getAnalystManagerPairingStatus");

		group.MapPost("/pairings/activation", ActivateAsync)
			.AddEndpointFilter<JsonOnlyFilter>()
			.RequireRateLimiting(RegistryRateLimiting.Activation)
			.WithName("activateAnalystManagerRegistration");
		return routes;
	}

	private static async Task<IResult> CreatePairingAsync(HttpContext http, StartPairingHandler handler, CancellationToken cancellationToken)
	{
		PairingRequestBody? body;
		try
		{
			body = await http.Request.ReadFromJsonAsync<PairingRequestBody>(cancellationToken);
		}
		catch (JsonException)
		{
			body = null;
		}

		if (body is null)
		{
			return ProblemResults.From(OperationFailure.Validation(new FieldViolation("body", "required")));
		}

		var m = body.DeviceMetadata;
		var result = await handler.HandleAsync(
			new StartPairingCommand(
				body.StampId, body.PublicKey, body.Nonce, m?.Os, m?.OsVersion, m?.Architecture, m?.ManagerVersion,
				m?.ClaimedKeyProtection, m?.ClaimedApplicationProfile, m?.HostLabel, http.Connection.RemoteIpAddress?.ToString()),
			cancellationToken);
		if (!result.IsSuccess)
		{
			return ProblemResults.From(result.Failure);
		}

		var v = result.Value;
		http.Response.Headers.CacheControl = "no-store";
		return Results.Json(new
		{
			pairingCode = v.PairingCode,
			deviceFingerprint = v.DeviceFingerprint,
			pollingHandle = v.PollingHandle,
			verificationUri = v.VerificationUri,
			expiresAt = v.ExpiresAt,
			pollingIntervalSeconds = v.PollingIntervalSeconds,
			stampId = v.StampId,
		}, statusCode: StatusCodes.Status201Created);
	}

	private static async Task<IResult> GetStatusAsync(HttpContext http, GetPairingStatusHandler handler, CancellationToken cancellationToken)
	{
		PollingHandleBody? body;
		try
		{
			body = await http.Request.ReadFromJsonAsync<PollingHandleBody>(cancellationToken);
		}
		catch (JsonException)
		{
			body = null;
		}

		http.Response.Headers.CacheControl = "no-store";
		var result = await handler.HandleAsync(body?.PollingHandle, cancellationToken);
		if (!result.IsSuccess)
		{
			return ProblemResults.From(result.Failure);
		}

		var v = result.Value;
		return Results.Json(new { status = v.Status, pollingIntervalSeconds = v.PollingIntervalSeconds, expiresAt = v.ExpiresAt, challenge = v.Challenge, challengeExpiresAt = v.ChallengeExpiresAt });
	}

	private static async Task<IResult> ActivateAsync(HttpContext http, ActivateRegistrationHandler handler, CancellationToken cancellationToken)
	{
		ActivationBody? body;
		try
		{
			body = await http.Request.ReadFromJsonAsync<ActivationBody>(cancellationToken);
		}
		catch (JsonException)
		{
			body = null;
		}

		http.Response.Headers.CacheControl = "no-store";
		var result = await handler.HandleAsync(body?.PollingHandle, body?.Proof, cancellationToken);
		if (!result.IsSuccess)
		{
			return ProblemResults.From(result.Failure);
		}

		var v = result.Value;
		return Results.Json(new { managerId = v.ManagerId, stampId = v.StampId, tokenEndpoint = v.TokenEndpoint, keyThumbprint = v.KeyThumbprint });
	}
}
