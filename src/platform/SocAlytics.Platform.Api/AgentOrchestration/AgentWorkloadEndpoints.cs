using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using SocAlytics.Platform.Api.Http;
using SocAlytics.Platform.Api.Registry;
using SocAlytics.Platform.Api.Security;
using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.AgentOrchestration.Execution;
using SocAlytics.Platform.Application.AgentOrchestration.Workloads;


namespace SocAlytics.Platform.Api.AgentOrchestration;

internal sealed record AgentWorkloadQueueCredentialsRequestDto(string? UserPublicKey);

internal sealed record AgentWorkloadQueueCredentialsDto(
	string UserJwt, DateTimeOffset ExpiresAt, string QueueUrl, string Stream, string Consumer, string InboxPrefix);

internal sealed record AgentClaimRequestDto(string? OperationId);

internal sealed record AgentLeaseRequestDto(string? FencingToken);

internal sealed record AgentLeaseResponseDto(Guid AttemptId, DateTimeOffset LeaseExpiresAt);

internal sealed record AgentCheckpointResponseDto(Guid AttemptId, int Sequence, bool Replayed);

internal sealed record AgentCompletionResponseDto(Guid AttemptId, Guid InvocationId, string InvocationState, bool Replayed);

internal static class AgentWorkloadEndpoints
{
	private static readonly JsonSerializerOptions BodyOptions = new(JsonSerializerDefaults.Web)
	{
		UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
	};

	public static IServiceCollection AddAgentWorkloadSecurity(this IServiceCollection services)
	{
		services.AddAuthentication()
			.AddScheme<AuthenticationSchemeOptions, AgentWorkloadDPoPAuthenticationHandler>(
				AgentWorkloadDPoPDefaults.AuthenticationScheme, null);
		services.AddAuthorizationBuilder()
			.AddPolicy(AuthorizationPolicyNames.AgentWorkload, policy => policy
				.AddAuthenticationSchemes(AgentWorkloadDPoPDefaults.AuthenticationScheme)
				.RequireAuthenticatedUser()
				.RequireClaim(AgentWorkloadClaimTypes.Scope, AgentWorkloadDPoPDefaults.ScopeValue));
		return services;
	}

	public static IEndpointRouteBuilder MapAgentWorkloadEndpoints(this IEndpointRouteBuilder routes)
	{
		routes.MapPost("/api/v1/agent-orchestration/workload/token", IssueTokenAsync)
			.AllowAnonymous()
			.AddEndpointFilter<RequireHttpsEndpointFilter>()
			.RequireRateLimiting(RegistryRateLimiting.Token)
			.WithTags("Agent workload")
			.WithName("issueAgentWorkloadToken");
		routes.MapPost("/api/v1/agent-orchestration/workload/queue-credentials", IssueQueueCredentialsAsync)
			.RequireAuthorization(AuthorizationPolicyNames.AgentWorkload)
			.AddEndpointFilter<RequireHttpsEndpointFilter>()
			.AddEndpointFilter<JsonOnlyFilter>()
			.WithTags("Agent workload")
			.WithName("issueAgentWorkloadQueueCredentials")
			.Accepts<AgentWorkloadQueueCredentialsRequestDto>("application/json")
			.Produces<AgentWorkloadQueueCredentialsDto>()
			.ProducesProblem(StatusCodes.Status400BadRequest)
			.ProducesProblem(StatusCodes.Status409Conflict);
		routes.MapPost("/api/v1/agent-orchestration/invocations/{invocationId:guid}/claims", ClaimAsync)
			.RequireAuthorization(AuthorizationPolicyNames.AgentWorkload)
			.AddEndpointFilter<RequireHttpsEndpointFilter>()
			.AddEndpointFilter<JsonOnlyFilter>()
			.WithTags("Agent workload")
			.WithName("claimAgentInvocation")
			.Accepts<AgentClaimRequestDto>("application/json")
			.Produces(StatusCodes.Status201Created, contentType: "application/json")
			.Produces(StatusCodes.Status200OK, contentType: "application/json")
			.ProducesProblem(StatusCodes.Status400BadRequest)
			.ProducesProblem(StatusCodes.Status404NotFound)
			.ProducesProblem(StatusCodes.Status409Conflict);
		routes.MapPost("/api/v1/agent-orchestration/attempts/{attemptId:guid}/completion", CompleteAsync)
			.RequireAuthorization(AuthorizationPolicyNames.AgentWorkload)
			.AddEndpointFilter<RequireHttpsEndpointFilter>()
			.AddEndpointFilter<JsonOnlyFilter>()
			.WithTags("Agent workload")
			.WithName("completeAgentAttempt")
			.Produces<AgentCompletionResponseDto>()
			.ProducesProblem(StatusCodes.Status400BadRequest)
			.ProducesProblem(StatusCodes.Status404NotFound)
			.ProducesProblem(StatusCodes.Status409Conflict);
		routes.MapPost("/api/v1/agent-orchestration/attempts/{attemptId:guid}/lease", RenewLeaseAsync)
			.RequireAuthorization(AuthorizationPolicyNames.AgentWorkload)
			.AddEndpointFilter<RequireHttpsEndpointFilter>()
			.AddEndpointFilter<JsonOnlyFilter>()
			.WithTags("Agent workload")
			.WithName("renewAgentAttemptLease")
			.Accepts<AgentLeaseRequestDto>("application/json")
			.Produces<AgentLeaseResponseDto>()
			.ProducesProblem(StatusCodes.Status400BadRequest)
			.ProducesProblem(StatusCodes.Status404NotFound)
			.ProducesProblem(StatusCodes.Status409Conflict);
		routes.MapPost("/api/v1/agent-orchestration/attempts/{attemptId:guid}/checkpoints", RecordCheckpointAsync)
			.RequireAuthorization(AuthorizationPolicyNames.AgentWorkload)
			.AddEndpointFilter<RequireHttpsEndpointFilter>()
			.AddEndpointFilter<JsonOnlyFilter>()
			.WithTags("Agent workload")
			.WithName("recordAgentCheckpoint")
			.Produces<AgentCheckpointResponseDto>()
			.ProducesProblem(StatusCodes.Status400BadRequest)
			.ProducesProblem(StatusCodes.Status404NotFound)
			.ProducesProblem(StatusCodes.Status409Conflict);
		routes.MapPost("/api/v1/agent-orchestration/attempts/{attemptId:guid}/failure", FailAsync)
			.RequireAuthorization(AuthorizationPolicyNames.AgentWorkload)
			.AddEndpointFilter<RequireHttpsEndpointFilter>()
			.AddEndpointFilter<JsonOnlyFilter>()
			.WithTags("Agent workload")
			.WithName("failAgentAttempt")
			.Produces<AgentCompletionResponseDto>()
			.ProducesProblem(StatusCodes.Status400BadRequest)
			.ProducesProblem(StatusCodes.Status404NotFound)
			.ProducesProblem(StatusCodes.Status409Conflict);
		return routes;
	}

	private static async Task<IResult> RenewLeaseAsync(
		Guid attemptId, HttpContext http, RenewAgentAttemptLeaseHandler handler, CancellationToken cancellationToken)
	{
		http.Response.Headers.CacheControl = "no-store";
		if (ClientId(http) is not { } clientId)
		{
			return ProblemResults.From(OperationFailure.Unauthenticated());
		}

		var body = await ReadBodyAsync<AgentLeaseRequestDto>(http, cancellationToken);
		if (string.IsNullOrWhiteSpace(body?.FencingToken) || body.FencingToken.Length > 255)
		{
			return ProblemResults.From(OperationFailure.Validation(new FieldViolation("fencingToken", "invalid")));
		}

		var result = await handler.HandleAsync(new RenewAgentAttemptLeaseCommand(attemptId, clientId, body.FencingToken), cancellationToken);
		return result.IsSuccess
			? Results.Ok(new AgentLeaseResponseDto(result.Value.AttemptId, result.Value.LeaseExpiresAt))
			: Failure(result.Failure);
	}

	private static async Task<IResult> RecordCheckpointAsync(
		Guid attemptId, HttpContext http, RecordAgentCheckpointHandler handler, CancellationToken cancellationToken)
	{
		http.Response.Headers.CacheControl = "no-store";
		if (ClientId(http) is not { } clientId)
		{
			return ProblemResults.From(OperationFailure.Unauthenticated());
		}

		if (await ReadObjectAsync(http, cancellationToken) is not { } body)
		{
			return ProblemResults.From(OperationFailure.Validation(new FieldViolation("body", "invalid")));
		}

		var result = await handler.HandleAsync(new RecordAgentCheckpointCommand(attemptId, clientId, body), cancellationToken);
		return result.IsSuccess
			? Results.Ok(new AgentCheckpointResponseDto(result.Value.AttemptId, result.Value.Sequence, result.Value.Replayed))
			: Failure(result.Failure);
	}

	private static async Task<IResult> FailAsync(
		Guid attemptId, HttpContext http, FailAgentAttemptHandler handler, CancellationToken cancellationToken)
	{
		http.Response.Headers.CacheControl = "no-store";
		if (ClientId(http) is not { } clientId)
		{
			return ProblemResults.From(OperationFailure.Unauthenticated());
		}

		if (await ReadObjectAsync(http, cancellationToken) is not { } body)
		{
			return ProblemResults.From(OperationFailure.Validation(new FieldViolation("body", "invalid")));
		}

		var result = await handler.HandleAsync(new FailAgentAttemptCommand(attemptId, clientId, body), cancellationToken);
		return result.IsSuccess
			? Results.Ok(new AgentCompletionResponseDto(
				result.Value.AttemptId, result.Value.InvocationId, result.Value.InvocationState, result.Value.Replayed))
			: Failure(result.Failure);
	}

	private static async Task<JsonElement?> ReadObjectAsync(HttpContext http, CancellationToken cancellationToken)
	{
		try
		{
			var body = await JsonSerializer.DeserializeAsync<JsonElement>(http.Request.Body, BodyOptions, cancellationToken);
			return body.ValueKind == JsonValueKind.Object ? body : null;
		}
		catch (JsonException)
		{
			return null;
		}
	}

	private static async Task<IResult> IssueQueueCredentialsAsync(
		HttpContext http, IssueAgentWorkloadQueueCredentialsHandler handler, CancellationToken cancellationToken)
	{
		http.Response.Headers.CacheControl = "no-store";
		var body = await ReadBodyAsync<AgentWorkloadQueueCredentialsRequestDto>(http, cancellationToken);
		var result = await handler.HandleAsync(new IssueAgentWorkloadQueueCredentialsCommand(body?.UserPublicKey), cancellationToken);
		return result.IsSuccess
			? Results.Ok(new AgentWorkloadQueueCredentialsDto(
				result.Value.UserJwt, result.Value.ExpiresAt, result.Value.QueueUrl, result.Value.Stream, result.Value.Consumer,
				result.Value.InboxPrefix))
			: Failure(result.Failure);
	}

	private static async Task<IResult> ClaimAsync(
		Guid invocationId, HttpContext http, ClaimAgentInvocationHandler handler, CancellationToken cancellationToken)
	{
		http.Response.Headers.CacheControl = "no-store";
		if (ClientId(http) is not { } clientId)
		{
			return ProblemResults.From(OperationFailure.Unauthenticated());
		}

		var body = await ReadBodyAsync<AgentClaimRequestDto>(http, cancellationToken);
		if (string.IsNullOrWhiteSpace(body?.OperationId) || body.OperationId.Length > 255)
		{
			return ProblemResults.From(OperationFailure.Validation(new FieldViolation("operationId", "invalid")));
		}

		var result = await handler.HandleAsync(new ClaimAgentInvocationCommand(invocationId, clientId, body.OperationId), cancellationToken);
		if (!result.IsSuccess)
		{
			return Failure(result.Failure);
		}

		var envelope = result.Value.Envelope;
		var attemptId = envelope.GetProperty("attemptId").GetGuid();
		if (result.Value.Replayed)
		{
			return Results.Json(envelope, statusCode: StatusCodes.Status200OK);
		}

		http.Response.Headers.Location = $"/api/v1/agent-orchestration/attempts/{attemptId}";
		return Results.Json(envelope, statusCode: StatusCodes.Status201Created);
	}

	private static async Task<IResult> CompleteAsync(
		Guid attemptId, HttpContext http, CompleteAgentAttemptHandler handler, CancellationToken cancellationToken)
	{
		http.Response.Headers.CacheControl = "no-store";
		if (ClientId(http) is not { } clientId)
		{
			return ProblemResults.From(OperationFailure.Unauthenticated());
		}

		JsonElement body;
		try
		{
			body = await JsonSerializer.DeserializeAsync<JsonElement>(http.Request.Body, BodyOptions, cancellationToken);
		}
		catch (JsonException)
		{
			return ProblemResults.From(OperationFailure.Validation(new FieldViolation("body", "invalid")));
		}

		if (body.ValueKind != JsonValueKind.Object)
		{
			return ProblemResults.From(OperationFailure.Validation(new FieldViolation("body", "invalid")));
		}

		var result = await handler.HandleAsync(new CompleteAgentAttemptCommand(attemptId, clientId, body), cancellationToken);
		return result.IsSuccess
			? Results.Ok(new AgentCompletionResponseDto(
				result.Value.AttemptId, result.Value.InvocationId, result.Value.InvocationState, result.Value.Replayed))
			: Failure(result.Failure);
	}

	private static string? ClientId(HttpContext http) =>
		http.User.FindFirst(AgentWorkloadClaimTypes.ClientId)?.Value is { Length: > 0 } id ? id : null;

	private static async Task<T?> ReadBodyAsync<T>(HttpContext http, CancellationToken cancellationToken)
		where T : class
	{
		try
		{
			return await JsonSerializer.DeserializeAsync<T>(http.Request.Body, BodyOptions, cancellationToken);
		}
		catch (JsonException)
		{
			return null;
		}
	}

	// The stale refusal carries its disposition as a problem member instead of a field error.
	private static IResult Failure(OperationFailure failure)
	{
		if (failure.Code == AgentExecutionCodes.AttemptStale
			&& failure.FieldViolations?.FirstOrDefault(v => v.Field == AgentExecutionCodes.DispositionField) is { } disposition)
		{
			return ProblemResults.Problem(
				StatusCodes.Status409Conflict, failure.Code, null,
				new Dictionary<string, string> { [AgentExecutionCodes.DispositionField] = disposition.Code });
		}

		return ProblemResults.From(failure);
	}

	private static async Task<IResult> IssueTokenAsync(
		HttpContext http, IssueAgentWorkloadTokenHandler handler, CancellationToken cancellationToken)
	{
		http.Response.Headers.CacheControl = "no-store";
		http.Response.Headers.Pragma = "no-cache";
		if (!http.Request.HasFormContentType)
		{
			return Error("invalid_request");
		}

		var form = await http.Request.ReadFormAsync(cancellationToken);
		if (form.Any(kv => kv.Value.Count > 1))
		{
			return Error("invalid_request");
		}

		string? Single(string key) => form.TryGetValue(key, out var v) && v.Count == 1 ? v[0] : null;
		var dpop = http.Request.Headers["DPoP"];
		var result = await handler.HandleAsync(
			new IssueAgentWorkloadTokenCommand(
				Single("grant_type"), Single("client_assertion_type"), Single("client_assertion"), Single("client_id"),
				Single("scope"), dpop.Count == 1 ? dpop[0] : null),
			cancellationToken);
		if (!result.Succeeded)
		{
			return Error(result.Error!);
		}

		return Results.Json(new
		{
			access_token = result.AccessToken,
			token_type = "DPoP",
			expires_in = result.ExpiresIn,
			scope = AgentWorkloadTokenResult.Scope,
		});
	}

	private static IResult Error(string error) =>
		Results.Json(new { error }, statusCode: StatusCodes.Status400BadRequest);
}
