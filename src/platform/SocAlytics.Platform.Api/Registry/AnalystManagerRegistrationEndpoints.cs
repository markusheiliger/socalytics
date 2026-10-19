using System.Text.Json;
using SocAlytics.Platform.Api.Http;
using SocAlytics.Platform.Api.Security;
using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.Registry.Decisions;
using SocAlytics.Platform.Application.Registry.StatusReporting;
using SocAlytics.Platform.Domain.Registry;

namespace SocAlytics.Platform.Api.Registry;

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
			.WithName("submitAnalystManagerPairingCode")
			.Accepts<PairingCodeSubmissionBody>("application/json")
			.Produces<AnalystManagerRegistrationDto>(StatusCodes.Status201Created)
			.ProducesProblem(StatusCodes.Status400BadRequest)
			.ProducesProblem(StatusCodes.Status401Unauthorized)
			.ProducesProblem(StatusCodes.Status403Forbidden)
			.ProducesProblem(StatusCodes.Status429TooManyRequests);
		group.MapGet("/analyst-manager-registrations", ListAsync)
			.WithName("listAnalystManagerRegistrations")
			.Produces<AnalystManagerRegistrationPageDto>()
			.ProducesProblem(StatusCodes.Status400BadRequest)
			.ProducesProblem(StatusCodes.Status401Unauthorized)
			.ProducesProblem(StatusCodes.Status403Forbidden);
		group.MapGet("/analyst-manager-registrations/{registrationId:guid}", GetAsync)
			.WithName("getAnalystManagerRegistration")
			.Produces<AnalystManagerRegistrationDto>()
			.ProducesProblem(StatusCodes.Status401Unauthorized)
			.ProducesProblem(StatusCodes.Status403Forbidden)
			.ProducesProblem(StatusCodes.Status404NotFound);
		group.MapPost("/analyst-manager-registrations/{registrationId:guid}/approval", ApproveAsync)
			.WithName("approveAnalystManagerRegistration")
			.Produces<AnalystManagerRegistrationDto>()
			.ProducesProblem(StatusCodes.Status401Unauthorized)
			.ProducesProblem(StatusCodes.Status403Forbidden)
			.ProducesProblem(StatusCodes.Status404NotFound)
			.ProducesProblem(StatusCodes.Status409Conflict);
		group.MapPost("/analyst-manager-registrations/{registrationId:guid}/rejection", RejectAsync)
			.AddEndpointFilter<JsonOnlyFilter>()
			.WithName("rejectAnalystManagerRegistration")
			.Accepts<ReasonRequest>("application/json")
			.Produces<AnalystManagerRegistrationDto>()
			.ProducesProblem(StatusCodes.Status400BadRequest)
			.ProducesProblem(StatusCodes.Status401Unauthorized)
			.ProducesProblem(StatusCodes.Status403Forbidden)
			.ProducesProblem(StatusCodes.Status404NotFound)
			.ProducesProblem(StatusCodes.Status409Conflict);
		group.MapPost("/analyst-manager-registrations/{registrationId:guid}/revocation", RevokeAsync)
			.AddEndpointFilter<JsonOnlyFilter>()
			.WithName("revokeAnalystManagerRegistration")
			.Accepts<ReasonRequest>("application/json")
			.Produces<AnalystManagerRegistrationDto>()
			.ProducesProblem(StatusCodes.Status400BadRequest)
			.ProducesProblem(StatusCodes.Status401Unauthorized)
			.ProducesProblem(StatusCodes.Status403Forbidden)
			.ProducesProblem(StatusCodes.Status404NotFound)
			.ProducesProblem(StatusCodes.Status409Conflict);
		return routes;
	}

	internal static AnalystManagerRegistrationDto ToDto(AnalystManagerRegistration r) => BuildDto(r, false);

	internal static AnalystManagerRegistrationDto ToDto(FleetEntry entry)
	{
		var dto = BuildDto(entry.Registration, entry.IncludesOperations);
		if (!entry.IncludesOperations)
		{
			return dto;
		}

		var status = entry.Status;
		return dto with
		{
			Status = status is null
				? null
				: new AnalystManagerStatusDto(
					status.Report.OperatingState.ToApiName(),
					status.Report.PausedUntil,
					status.Report.RuntimeUnavailableReason,
					status.Report.ActiveAnalysts,
					status.Report.MaxAnalysts,
					status.Report.ManagerVersion,
					status.ReceivedAt),
			Connectivity = entry.Connectivity.ToApiName(),
			MinimumManagerVersion = entry.MinimumManagerVersion,
			BelowMinimumVersion = entry.BelowMinimumVersion,
		};
	}

	private static AnalystManagerRegistrationDto BuildDto(AnalystManagerRegistration r, bool includesOperations)
	{
		var m = r.DeviceMetadata;
		return new AnalystManagerRegistrationDto(
			r.Id,
			r.ManagerId,
			r.StampId,
			r.State.ToApiName(),
			r.KeyThumbprint,
			r.DeviceFingerprint.Value,
			// Claimed by the Manager, not attested by the platform.
			new AnalystManagerDeviceMetadataDto(
				m.Os, m.OsVersion, m.Architecture, m.ManagerVersion, m.ClaimedKeyProtection.ToApiName(),
				m.ClaimedApplicationProfile?.ToApiName(), m.HostLabel),
			new AnalystManagerPairingOriginDto(r.PairingSourceAddress, r.CreatedAt),
			r.State == RegistrationState.PendingApproval ? ApprovalNotice : null,
			r.SubmittedBy,
			r.SubmittedAt,
			r.DecidedBy,
			r.DecidedAt,
			r.DecisionReason,
			r.ActivatedAt,
			r.EndedAt,
			r.EndReason,
			r.RevokedBy,
			r.PairingExpiresAt,
			r.ApprovalExpiresAt,
			r.ActivationExpiresAt,
			r.CreatedAt,
			r.Version)
		{
			IncludesOperations = includesOperations,
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
			? Results.Json(new AnalystManagerRegistrationPageDto(
				result.Value.Items.Select(i => ToDto(i)).ToArray(), result.Value.ContinuationToken))
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

		http.Response.Headers.ETag = IfMatchHeader.Format(result.Value.Registration.Version);
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

	private static async Task<IResult> RevokeAsync(
		Guid registrationId, HttpContext http, RevokeRegistrationHandler handler, CancellationToken cancellationToken)
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
