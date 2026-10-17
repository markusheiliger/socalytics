using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using SocAlytics.Platform.Api.Http;
using SocAlytics.Platform.Api.Security;
using SocAlytics.Platform.Application.Registry.MachineAccess;
using SocAlytics.Platform.Application.Registry.QueueCredentials;
using SocAlytics.Platform.Application.Registry.QueueDepth;

namespace SocAlytics.Platform.Api.Registry;

internal sealed record QueueCredentialsRequestBody(string? UserPublicKey, List<CapabilityReference>? Capabilities);

internal static class AnalystManagerSelfEndpoints
{
	public static IEndpointRouteBuilder MapAnalystManagerSelfEndpoints(this IEndpointRouteBuilder routes)
	{
		routes.MapGet("/api/v1/analyst-managers/self/registration", GetAsync)
			.RequireAuthorization(AuthorizationPolicyNames.AnalystManager)
			.AddEndpointFilter<RequireHttpsEndpointFilter>()
			.WithTags("Analyst Manager self")
			.WithName("getOwnAnalystManagerRegistration");
		routes.MapPost("/api/v1/analyst-managers/self/status", ReportStatusAsync)
			.RequireAuthorization(AuthorizationPolicyNames.AnalystManager)
			.AddEndpointFilter<RequireHttpsEndpointFilter>()
			.WithTags("Analyst Manager self")
			.WithName("reportAnalystManagerStatus");
		routes.MapPost("/api/v1/analyst-managers/self/unregistration", BeginUnregistrationAsync)
			.RequireAuthorization(AuthorizationPolicyNames.AnalystManager)
			.AddEndpointFilter<RequireHttpsEndpointFilter>()
			.WithTags("Analyst Manager self")
			.WithName("beginAnalystManagerUnregistration");
		routes.MapPost("/api/v1/analyst-managers/self/unregistration/completion", CompleteUnregistrationAsync)
			.RequireAuthorization(AuthorizationPolicyNames.AnalystManager)
			.AddEndpointFilter<RequireHttpsEndpointFilter>()
			.WithTags("Analyst Manager self")
			.WithName("completeAnalystManagerUnregistration");
		routes.MapPost("/api/v1/analyst-managers/self/queue-credentials", IssueQueueCredentialsAsync)
			.RequireAuthorization(AuthorizationPolicyNames.AnalystManager)
			.AddEndpointFilter<RequireHttpsEndpointFilter>()
			.AddEndpointFilter<JsonOnlyFilter>()
			.WithTags("Analyst Manager self")
			.WithName("issueAnalystManagerQueueCredentials");
		routes.MapGet("/api/v1/analyst-managers/self/queue-depth", ReadQueueDepthAsync)
			.RequireAuthorization(AuthorizationPolicyNames.AnalystManager)
			.AddEndpointFilter<RequireHttpsEndpointFilter>()
			.WithTags("Analyst Manager self")
			.WithName("getAnalystManagerQueueDepth");
		return routes;
	}

	private static async Task<IResult> ReadQueueDepthAsync(
		HttpContext http, ReadQueueDepthHandler handler, [FromQuery(Name = "capability")] string[]? capability, CancellationToken cancellationToken)
	{
		http.Response.Headers.CacheControl = "no-store";
		if (!Guid.TryParse(http.User.FindFirst(AnalystManagerClaimTypes.RegistrationId)?.Value, out var registrationId))
		{
			return ProblemResults.From(Application.Abstractions.OperationFailure.Unauthenticated());
		}

		var result = await handler.HandleAsync(new ReadQueueDepthCommand(registrationId, capability), cancellationToken);
		return result.IsSuccess ? Results.Ok(result.Value) : ProblemResults.From(result.Failure);
	}

	private sealed record StatusReportBody(
		string? OperatingState, DateTimeOffset? PausedUntil, string? RuntimeUnavailableReason,
		int? ActiveAnalysts, int? MaxAnalysts, string? ManagerVersion);

	private static async Task<IResult> ReportStatusAsync(HttpContext http, ReportStatusHandler handler, CancellationToken cancellationToken)
	{
		http.Response.Headers.CacheControl = "no-store";
		if (!Guid.TryParse(http.User.FindFirst(AnalystManagerClaimTypes.RegistrationId)?.Value, out var registrationId))
		{
			return ProblemResults.From(Application.Abstractions.OperationFailure.Unauthenticated());
		}

		StatusReportBody? body;
		try
		{
			body = await http.Request.ReadFromJsonAsync<StatusReportBody>(cancellationToken);
		}
		catch (System.Text.Json.JsonException)
		{
			body = null;
		}

		if (body is null)
		{
			return ProblemResults.From(Application.Abstractions.OperationFailure.Validation(
				new Application.Abstractions.FieldViolation("body", "required")));
		}

		var result = await handler.HandleAsync(
			registrationId,
			new ReportStatusCommand(
				body.OperatingState, body.PausedUntil, body.RuntimeUnavailableReason,
				body.ActiveAnalysts, body.MaxAnalysts, body.ManagerVersion),
			cancellationToken);
		return result.IsSuccess ? Results.Ok(result.Value) : ProblemResults.From(result.Failure);
	}

	private static async Task<IResult> IssueQueueCredentialsAsync(HttpContext http, IssueQueueCredentialsHandler handler, CancellationToken cancellationToken)
	{
		http.Response.Headers.CacheControl = "no-store";
		if (!Guid.TryParse(http.User.FindFirst(AnalystManagerClaimTypes.RegistrationId)?.Value, out var registrationId))
		{
			return ProblemResults.From(Application.Abstractions.OperationFailure.Unauthenticated());
		}

		QueueCredentialsRequestBody? body;
		try
		{
			body = await http.Request.ReadFromJsonAsync<QueueCredentialsRequestBody>(cancellationToken);
		}
		catch (JsonException)
		{
			return ProblemResults.From(Application.Abstractions.OperationFailure.Validation("invalid-capabilities"));
		}

		if (body is null)
		{
			return ProblemResults.From(Application.Abstractions.OperationFailure.Validation("invalid-user-key"));
		}

		var result = await handler.HandleAsync(
			new IssueQueueCredentialsCommand(registrationId, body.UserPublicKey, body.Capabilities), cancellationToken);
		return result.IsSuccess
			? Results.Json(result.Value, statusCode: StatusCodes.Status201Created)
			: ProblemResults.From(result.Failure);
	}

	private static async Task<IResult> BeginUnregistrationAsync(HttpContext http, BeginUnregistrationHandler handler, CancellationToken cancellationToken)
	{
		http.Response.Headers.CacheControl = "no-store";
		if (!Guid.TryParse(http.User.FindFirst(AnalystManagerClaimTypes.RegistrationId)?.Value, out var registrationId))
		{
			return ProblemResults.From(Application.Abstractions.OperationFailure.Unauthenticated());
		}

		var result = await handler.HandleAsync(registrationId, cancellationToken);
		return result.IsSuccess ? Results.Ok(result.Value) : ProblemResults.From(result.Failure);
	}

	private static async Task<IResult> CompleteUnregistrationAsync(HttpContext http, CompleteUnregistrationHandler handler, CancellationToken cancellationToken)
	{
		http.Response.Headers.CacheControl = "no-store";
		if (!Guid.TryParse(http.User.FindFirst(AnalystManagerClaimTypes.RegistrationId)?.Value, out var registrationId))
		{
			return ProblemResults.From(Application.Abstractions.OperationFailure.Unauthenticated());
		}

		var result = await handler.HandleAsync(registrationId, cancellationToken);
		return result.IsSuccess ? Results.Ok(result.Value) : ProblemResults.From(result.Failure);
	}

	private static async Task<IResult> GetAsync(HttpContext http, GetOwnRegistrationHandler handler, CancellationToken cancellationToken)
	{
		http.Response.Headers.CacheControl = "no-store";
		if (!Guid.TryParse(http.User.FindFirst(AnalystManagerClaimTypes.RegistrationId)?.Value, out var registrationId))
		{
			return ProblemResults.From(Application.Abstractions.OperationFailure.Unauthenticated());
		}

		var result = await handler.HandleAsync(registrationId, cancellationToken);
		return result.IsSuccess ? Results.Ok(result.Value) : ProblemResults.From(result.Failure);
	}
}
