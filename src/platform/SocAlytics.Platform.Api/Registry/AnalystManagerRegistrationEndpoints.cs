using System.Text.Json;
using SocAlytics.Platform.Api.Http;
using SocAlytics.Platform.Api.Security;
using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.Registry.Decisions;
using SocAlytics.Platform.Domain.Registry;

namespace SocAlytics.Platform.Api.Registry;

internal sealed record PairingCodeSubmissionBody(string? PairingCode, string? DeviceFingerprint);

internal sealed record ReasonRequest(string? Reason);

internal static class AnalystManagerRegistrationEndpoints
{
	public const string ApprovalNotice =
		"Approve only an Analyst Manager you can physically identify by the device fingerprint shown on it.";

	private const string Path = MemberApiRouteGroup.Prefix + "/analyst-manager-registrations";

	public static IEndpointRouteBuilder MapAnalystManagerRegistrationEndpoints(this IEndpointRouteBuilder routes)
	{
		var group = routes.MapMemberApi().WithTags("Analyst Manager registrations");
		group.MapPost("/analyst-manager-registrations", SubmitAsync)
			.AddEndpointFilter<JsonOnlyFilter>()
			.RequireRateLimiting(RegistryRateLimiting.CodeSubmission)
			.WithName("submitAnalystManagerPairingCode");
		group.MapGet("/analyst-manager-registrations", ListAsync)
			.WithName("listAnalystManagerRegistrations");
		group.MapGet("/analyst-manager-registrations/{registrationId:guid}", GetAsync)
			.WithName("getAnalystManagerRegistration");
		group.MapPost("/analyst-manager-registrations/{registrationId:guid}/approval", ApproveAsync)
			.WithName("approveAnalystManagerRegistration");
		group.MapPost("/analyst-manager-registrations/{registrationId:guid}/rejection", RejectAsync)
			.AddEndpointFilter<JsonOnlyFilter>()
			.WithName("rejectAnalystManagerRegistration");
		return routes;
	}

	internal static object ToDto(AnalystManagerRegistration r)
	{
		var m = r.DeviceMetadata;
		return new
		{
			registrationId = r.Id,
			managerId = r.ManagerId,
			stampId = r.StampId,
			state = r.State.ToApiName(),
			keyThumbprint = r.KeyThumbprint,
			deviceFingerprint = r.DeviceFingerprint.Value,
			// Claimed by the Manager, not attested by the platform.
			deviceMetadata = new
			{
				os = m.Os,
				osVersion = m.OsVersion,
				architecture = m.Architecture,
				managerVersion = m.ManagerVersion,
				claimedKeyProtection = m.ClaimedKeyProtection.ToApiName(),
				hostLabel = m.HostLabel,
			},
			pairingOrigin = new { sourceAddress = r.PairingSourceAddress, requestedAt = r.CreatedAt },
			approvalNotice = r.State == RegistrationState.PendingApproval ? ApprovalNotice : null,
			submittedBy = r.SubmittedBy,
			submittedAt = r.SubmittedAt,
			decidedBy = r.DecidedBy,
			decidedAt = r.DecidedAt,
			decisionReason = r.DecisionReason,
			activatedAt = r.ActivatedAt,
			endedAt = r.EndedAt,
			endReason = r.EndReason,
			revokedBy = r.RevokedBy,
			pairingExpiresAt = r.PairingExpiresAt,
			approvalExpiresAt = r.ApprovalExpiresAt,
			activationExpiresAt = r.ActivationExpiresAt,
			createdAt = r.CreatedAt,
			version = r.Version,
		};
	}

	private static async Task<IResult> SubmitAsync(HttpContext http, SubmitPairingCodeHandler handler, CancellationToken cancellationToken)
	{
		PairingCodeSubmissionBody? body;
		try
		{
			body = await http.Request.ReadFromJsonAsync<PairingCodeSubmissionBody>(cancellationToken);
		}
		catch (JsonException)
		{
			body = null;
		}

		var result = await handler.HandleAsync(new SubmitPairingCodeCommand(body?.PairingCode, body?.DeviceFingerprint), cancellationToken);
		if (!result.IsSuccess)
		{
			return ProblemResults.From(result.Failure);
		}

		var r = result.Value;
		http.Response.Headers.ETag = IfMatchHeader.Format(r.Version);
		http.Response.Headers.Location = $"{Path}/{r.Id}";
		return Results.Json(ToDto(r), statusCode: StatusCodes.Status201Created);
	}

	private static async Task<IResult> ListAsync(
		string? state, string? continuationToken, ListRegistrationsHandler handler, CancellationToken cancellationToken)
	{
		var result = await handler.HandleAsync(state, continuationToken, cancellationToken);
		return result.IsSuccess
			? Results.Json(new { items = result.Value.Items.Select(ToDto).ToArray(), continuationToken = result.Value.ContinuationToken })
			: ProblemResults.From(result.Failure);
	}

	private static async Task<IResult> GetAsync(
		Guid registrationId, HttpContext http, GetRegistrationHandler handler, CancellationToken cancellationToken)
	{
		var result = await handler.HandleAsync(registrationId, cancellationToken);
		if (!result.IsSuccess)
		{
			return ProblemResults.From(result.Failure);
		}

		http.Response.Headers.ETag = IfMatchHeader.Format(result.Value.Version);
		return Results.Json(ToDto(result.Value));
	}

	private static async Task<IResult> ApproveAsync(
		Guid registrationId, HttpContext http, ApproveRegistrationHandler handler, CancellationToken cancellationToken)
	{
		var result = await handler.HandleAsync(registrationId, cancellationToken);
		return Decided(http, result);
	}

	private static async Task<IResult> RejectAsync(
		Guid registrationId, HttpContext http, RejectRegistrationHandler handler, CancellationToken cancellationToken)
	{
		ReasonRequest? body;
		try
		{
			body = await http.Request.ReadFromJsonAsync<ReasonRequest>(cancellationToken);
		}
		catch (JsonException)
		{
			body = null;
		}

		return Decided(http, await handler.HandleAsync(registrationId, body?.Reason, cancellationToken));
	}

	private static IResult Decided(HttpContext http, OperationResult<AnalystManagerRegistration> result)
	{
		if (!result.IsSuccess)
		{
			return ProblemResults.From(result.Failure);
		}

		http.Response.Headers.ETag = IfMatchHeader.Format(result.Value.Version);
		return Results.Json(ToDto(result.Value));
	}
}
