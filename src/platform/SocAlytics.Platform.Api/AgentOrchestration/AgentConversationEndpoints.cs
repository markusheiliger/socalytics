using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.OpenApi;
using SocAlytics.Platform.Api.Http;
using SocAlytics.Platform.Api.Security;
using SocAlytics.Platform.Application.AgentOrchestration;
using SocAlytics.Platform.Application.AgentOrchestration.Conversations;
using SocAlytics.Platform.Application.AgentOrchestration.Invocations;
using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Domain.AgentOrchestration;

namespace SocAlytics.Platform.Api.AgentOrchestration;

internal sealed record AvailableAgentDto(string AgentId, string Group, string Domain, string DisplayName, string EnabledVersion);

internal sealed record AvailableAgentsDto(IReadOnlyList<AvailableAgentDto> Agents);

internal sealed record AgentParticipantDto(string AgentId, DateTimeOffset AddedAt);

internal sealed record AgentConversationDto(
	Guid ConversationId, Guid MatchId, Guid AnalysisRunId, bool AnalysisRunIsCurrent, Guid TeamId, string State,
	IReadOnlyList<AgentParticipantDto> Participants, DateTimeOffset CreatedAt, long Version)
{
	public static AgentConversationDto From(AgentConversationView v)
	{
		var c = v.Conversation;
		return new(
			c.Id, c.MatchId, c.AnalysisRunId, v.AnalysisRunIsCurrent, c.TeamId, c.State,
			[.. c.Participants.Select(p => new AgentParticipantDto(p.AgentId, p.AddedAt))], c.CreatedAt, c.Version);
	}
}

internal sealed record AgentConversationListDto(IReadOnlyList<AgentConversationDto> Conversations);

internal sealed record CreateAgentConversationRequestDto(Guid? AnalysisRunId);

internal sealed record AddAgentConversationParticipantRequestDto(string? AgentId);

internal sealed record SubmitAgentInvocationRequestDto(string? AgentId, string? Question);

internal sealed record AgentInvocationAcceptedDto(Guid ConversationId, Guid InvocationId, string StatusLocation, int RetryAfterSeconds);

internal sealed record AgentTurnDto(
	Guid InvocationId, string AgentId, string Question, string State, DateTimeOffset SubmittedAt, DateTimeOffset? EndedAt,
	string? FailureReason, string? Advice);

internal sealed record AgentTurnPageDto(IReadOnlyList<AgentTurnDto> Turns, string? NextCursor);

internal sealed record AgentInvocationDto(
	Guid InvocationId, Guid ConversationId, string AgentId, string Route, string State, int AttemptCount, string? FailureReason,
	string? Advice, DateTimeOffset SubmittedAt, DateTimeOffset? EndedAt, int? RetryAfterSeconds);

internal sealed record AgentLineageEntryDto(
	int Sequence, string Kind, Guid? AttemptId, int? AttemptNumber, DateTimeOffset RecordedAt,
	IReadOnlyDictionary<string, JsonElement> Details);

internal sealed record AgentLineageAdviceDto(
	Guid AttemptId, string Advice, JsonElement ExecutionReferences, DateTimeOffset AcceptedAt);

internal sealed record AgentInvocationLineageDto(
	Guid InvocationId, string PolicyVersion, IReadOnlyList<AgentLineageEntryDto> Entries, AgentLineageAdviceDto? Advice);

internal static class AgentConversationEndpoints
{
	private static readonly JsonSerializerOptions BodyOptions = new(JsonSerializerDefaults.Web)
	{
		UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
	};

	private static TBuilder WithIdempotencyKeyParameter<TBuilder>(this TBuilder builder)
		where TBuilder : IEndpointConventionBuilder =>
		builder.AddOpenApiOperationTransformer((operation, _, _) =>
		{
			operation.Parameters ??= [];
			operation.Parameters.Add(new Microsoft.OpenApi.OpenApiParameter
			{
				Name = "Idempotency-Key",
				In = Microsoft.OpenApi.ParameterLocation.Header,
				Required = true,
				Schema = new Microsoft.OpenApi.OpenApiSchema { Type = Microsoft.OpenApi.JsonSchemaType.String, MinLength = 1, MaxLength = 255 },
			});
			return Task.CompletedTask;
		});

	public static IEndpointRouteBuilder MapAgentConversationEndpoints(this IEndpointRouteBuilder routes)
	{
		var members = routes.MapMemberApi();
		members.MapGet("/agent-catalog/agents", ListAvailableAgentsAsync)
			.WithName("listAvailableAgents")
			.WithTags("Agent conversations")
			.Produces<AvailableAgentsDto>()
			.ProducesProblem(StatusCodes.Status401Unauthorized)
			.ProducesProblem(StatusCodes.Status403Forbidden);
		members.MapPost("/matches/{matchId:guid}/agent-conversations", CreateAsync)
			.AddEndpointFilter<JsonOnlyFilter>()
			.WithName("createAgentConversation")
			.WithTags("Agent conversations")
			.WithIdempotencyKeyParameter()
			.Accepts<CreateAgentConversationRequestDto>("application/json")
			.Produces<AgentConversationDto>(StatusCodes.Status201Created)
			.Produces<AgentConversationDto>()
			.ProducesProblem(StatusCodes.Status400BadRequest)
			.ProducesProblem(StatusCodes.Status403Forbidden)
			.ProducesProblem(StatusCodes.Status404NotFound)
			.ProducesProblem(StatusCodes.Status409Conflict);
		members.MapGet("/matches/{matchId:guid}/agent-conversations", ListConversationsAsync)
			.WithName("listAgentConversations")
			.WithTags("Agent conversations")
			.Produces<AgentConversationListDto>()
			.ProducesProblem(StatusCodes.Status404NotFound);
		members.MapGet("/agent-conversations/{conversationId:guid}", GetConversationAsync)
			.WithName("getAgentConversation")
			.WithTags("Agent conversations")
			.Produces<AgentConversationDto>()
			.Produces(StatusCodes.Status304NotModified)
			.ProducesProblem(StatusCodes.Status404NotFound);
		members.MapPost("/agent-conversations/{conversationId:guid}/participants", AddParticipantAsync)
			.AddEndpointFilter<JsonOnlyFilter>()
			.WithName("addAgentConversationParticipant")
			.WithTags("Agent conversations")
			.Accepts<AddAgentConversationParticipantRequestDto>("application/json")
			.Produces<AgentConversationDto>()
			.ProducesProblem(StatusCodes.Status400BadRequest)
			.ProducesProblem(StatusCodes.Status404NotFound)
			.ProducesProblem(StatusCodes.Status409Conflict)
			.ProducesProblem(StatusCodes.Status412PreconditionFailed)
			.ProducesProblem(StatusCodes.Status428PreconditionRequired);
		members.MapPost("/agent-conversations/{conversationId:guid}/invocations", SubmitAsync)
			.AddEndpointFilter<JsonOnlyFilter>()
			.WithName("submitAgentInvocation")
			.WithTags("Agent conversations")
			.WithIdempotencyKeyParameter()
			.Accepts<SubmitAgentInvocationRequestDto>("application/json")
			.Produces<AgentInvocationAcceptedDto>(StatusCodes.Status202Accepted)
			.ProducesProblem(StatusCodes.Status400BadRequest)
			.ProducesProblem(StatusCodes.Status403Forbidden)
			.ProducesProblem(StatusCodes.Status404NotFound)
			.ProducesProblem(StatusCodes.Status409Conflict)
			.ProducesProblem(StatusCodes.Status429TooManyRequests);
		members.MapGet("/agent-conversations/{conversationId:guid}/turns", ListTurnsAsync)
			.WithName("listAgentConversationTurns")
			.WithTags("Agent conversations")
			.Produces<AgentTurnPageDto>()
			.Produces(StatusCodes.Status304NotModified)
			.ProducesProblem(StatusCodes.Status400BadRequest)
			.ProducesProblem(StatusCodes.Status404NotFound);
		members.MapGet("/agent-invocations/{invocationId:guid}", GetInvocationAsync)
			.WithName("getAgentInvocation")
			.WithTags("Agent conversations")
			.Produces<AgentInvocationDto>()
			.Produces(StatusCodes.Status304NotModified)
			.ProducesProblem(StatusCodes.Status404NotFound);
		members.MapGet("/agent-invocations/{invocationId:guid}/lineage", GetInvocationLineageAsync)
			.WithName("getAgentInvocationLineage")
			.WithTags("Agent conversations")
			.Produces<AgentInvocationLineageDto>()
			.Produces(StatusCodes.Status304NotModified)
			.ProducesProblem(StatusCodes.Status404NotFound);
		return routes;
	}

	private static async Task<IResult> ListAvailableAgentsAsync(ListAvailableAgentsHandler handler, CancellationToken cancellationToken)
	{
		var result = await handler.HandleAsync(cancellationToken);
		return result.IsSuccess
			? Results.Ok(new AvailableAgentsDto([.. result.Value.Select(a => new AvailableAgentDto(a.AgentId, a.Group, a.Domain, a.DisplayName, a.EnabledVersion))]))
			: ProblemResults.From(result.Failure);
	}

	private static async Task<IResult> CreateAsync(
		Guid matchId, HttpContext http, CreateAgentConversationHandler handler, CancellationToken cancellationToken)
	{
		var keyHeader = http.Request.Headers["Idempotency-Key"];
		var body = await ReadBodyAsync<CreateAgentConversationRequestDto>(http, cancellationToken);
		var result = await handler.HandleAsync(
			new CreateAgentConversationCommand(matchId, keyHeader.Count == 1 ? keyHeader[0] : null, body?.AnalysisRunId), cancellationToken);
		if (!result.IsSuccess)
		{
			return Failure(result.Failure);
		}

		var view = result.Value.Conversation;
		http.Response.Headers.ETag = IfMatchHeader.Format(view.Conversation.Version);
		var dto = AgentConversationDto.From(view);
		return result.Value.Replayed
			? Results.Ok(dto)
			: Results.Created($"/api/v1/agent-conversations/{dto.ConversationId}", dto);
	}

	private static async Task<IResult> ListConversationsAsync(Guid matchId, ListAgentConversationsHandler handler, CancellationToken cancellationToken)
	{
		var result = await handler.HandleAsync(new ListAgentConversationsQuery(matchId), cancellationToken);
		return result.IsSuccess
			? Results.Ok(new AgentConversationListDto([.. result.Value.Items.Select(AgentConversationDto.From)]))
			: ProblemResults.From(result.Failure);
	}

	private static async Task<IResult> GetConversationAsync(
		Guid conversationId, HttpContext http, GetAgentConversationHandler handler, CancellationToken cancellationToken)
	{
		var result = await handler.HandleAsync(new GetAgentConversationQuery(conversationId), cancellationToken);
		if (!result.IsSuccess)
		{
			return ProblemResults.From(result.Failure);
		}

		// The current flag changes without a version change, so it is part of the validator.
		var etag = StrongEtag($"{result.Value.Conversation.Version}:{result.Value.AnalysisRunIsCurrent}");
		return Conditional(http, etag, () => AgentConversationDto.From(result.Value));
	}

	private static async Task<IResult> AddParticipantAsync(
		Guid conversationId, HttpContext http, AddAgentConversationParticipantHandler handler, CancellationToken cancellationToken)
	{
		var expected = IfMatchHeader.Parse(http.Request);
		if (!expected.IsSuccess)
		{
			return ProblemResults.From(expected.Failure);
		}

		var body = await ReadBodyAsync<AddAgentConversationParticipantRequestDto>(http, cancellationToken);
		var result = await handler.HandleAsync(
			new AddAgentConversationParticipantCommand(conversationId, expected.Value, body?.AgentId), cancellationToken);
		if (!result.IsSuccess)
		{
			return ProblemResults.From(result.Failure);
		}

		http.Response.Headers.ETag = IfMatchHeader.Format(result.Value.Conversation.Version);
		return Results.Ok(AgentConversationDto.From(result.Value));
	}

	private static async Task<IResult> SubmitAsync(
		Guid conversationId, HttpContext http, SubmitAgentInvocationHandler handler, CancellationToken cancellationToken)
	{
		var keyHeader = http.Request.Headers["Idempotency-Key"];
		if (keyHeader.Count == 0)
		{
			return ProblemResults.Problem(SharedProblemCodes.StatusOf(SharedProblemCodes.IdempotencyKeyMissing), SharedProblemCodes.IdempotencyKeyMissing);
		}

		var body = await ReadBodyAsync<SubmitAgentInvocationRequestDto>(http, cancellationToken);
		var result = await handler.HandleAsync(
			new SubmitAgentInvocationCommand(conversationId, keyHeader.Count == 1 ? keyHeader[0] : null, body?.AgentId, body?.Question),
			cancellationToken);
		if (!result.IsSuccess)
		{
			return ProblemResults.From(result.Failure);
		}

		var accepted = result.Value;
		var location = $"/api/v1/agent-invocations/{accepted.InvocationId}";
		http.Response.Headers.Location = location;
		http.Response.Headers.RetryAfter = accepted.RetryAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
		return Results.Json(
			new AgentInvocationAcceptedDto(accepted.ConversationId, accepted.InvocationId, location, accepted.RetryAfterSeconds),
			statusCode: StatusCodes.Status202Accepted);
	}

	private static async Task<IResult> ListTurnsAsync(
		Guid conversationId, string? cursor, int? limit, HttpContext http, ListAgentConversationTurnsHandler handler,
		CancellationToken cancellationToken)
	{
		var result = await handler.HandleAsync(new ListAgentConversationTurnsQuery(conversationId, cursor, limit), cancellationToken);
		if (!result.IsSuccess)
		{
			return ProblemResults.From(result.Failure);
		}

		var page = result.Value;
		var basis = string.Join(
			'|',
			page.Turns.Select(t => $"{t.InvocationId:N}:{(int)t.State}:{t.Advice?.AcceptedAt.UtcTicks}")
				.Append(page.NextCursor ?? string.Empty));
		return Conditional(http, StrongEtag(basis), () => new AgentTurnPageDto(
			[.. page.Turns.Select(t => new AgentTurnDto(
				t.InvocationId, t.AgentId, t.Question, AgentPersistedNames.ToWireName(t.State), t.SubmittedAt,
				t.Advice?.AcceptedAt, FailureName(t.FailureReason), t.Advice?.Advice))],
			page.NextCursor));
	}

	private static async Task<IResult> GetInvocationAsync(
		Guid invocationId, HttpContext http, GetAgentInvocationHandler handler, CancellationToken cancellationToken)
	{
		var result = await handler.HandleAsync(new GetAgentInvocationQuery(invocationId), cancellationToken);
		if (!result.IsSuccess)
		{
			return ProblemResults.From(result.Failure);
		}

		var view = result.Value;
		var i = view.Invocation;
		if (view.RetryAfterSeconds is { } retryAfter)
		{
			http.Response.Headers.RetryAfter = retryAfter.ToString(System.Globalization.CultureInfo.InvariantCulture);
		}

		return Conditional(http, StrongEtag($"{i.ProgressVersion}:{i.Version}"), () => new AgentInvocationDto(
			i.Id, i.ConversationId, i.AgentId, AgentPersistedNames.ToPersistedName(i.Route), AgentPersistedNames.ToWireName(i.State),
			i.AttemptsUsed, FailureName(i.FailureReason), view.Advice?.Advice, i.SubmittedAt, i.EndedAt, view.RetryAfterSeconds));
	}

	private static async Task<IResult> GetInvocationLineageAsync(
		Guid invocationId, HttpContext http, GetAgentInvocationLineageHandler handler, CancellationToken cancellationToken)
	{
		var result = await handler.HandleAsync(new GetAgentInvocationLineageQuery(invocationId), cancellationToken);
		if (!result.IsSuccess)
		{
			return ProblemResults.From(result.Failure);
		}

		var view = result.Value;
		// Lineage rows are append-only, so the entry count and the advice acceptance time identify its content.
		var basis = $"{view.Invocation.Id:N}:{view.Entries.Count}:{view.Entries.Select(e => e.Sequence).DefaultIfEmpty().Max()}:{view.Advice?.AcceptedAt.UtcTicks}";
		return Conditional(http, StrongEtag(basis), () => new AgentInvocationLineageDto(
			view.Invocation.Id, view.Invocation.PolicyVersion,
			[.. view.Entries.Select(e => new AgentLineageEntryDto(e.Sequence, e.Kind, e.AttemptId, e.AttemptNumber, e.RecordedAt, e.Details))],
			view.Advice is { } a
				? new AgentLineageAdviceDto(a.AttemptId, a.Advice, JsonDocument.Parse(a.ExecutionReferencesJson).RootElement.Clone(), a.AcceptedAt)
				: null));
	}

	private static string? FailureName(AgentFailureReason? reason) =>
		reason is { } r ? AgentPersistedNames.ToPersistedName(r) : null;

	private static string StrongEtag(string basis) =>
		$"\"{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(basis)))[..32].ToLowerInvariant()}\"";

	private static IResult Conditional<T>(HttpContext http, string etag, Func<T> body)
	{
		http.Response.Headers.ETag = etag;
		var candidates = http.Request.Headers.IfNoneMatch.ToString()
			.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
		return candidates.Any(c => c == "*" || c == etag)
			? Results.StatusCode(StatusCodes.Status304NotModified)
			: Results.Ok(body());
	}

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

	private static IResult Failure(OperationFailure failure) =>
		failure.Code == SharedProblemCodes.IdempotencyKeyMissing || failure.Code == SharedProblemCodes.IdempotencyKeyReused
			? ProblemResults.Problem(SharedProblemCodes.StatusOf(failure.Code), failure.Code)
			: ProblemResults.From(failure);
}
