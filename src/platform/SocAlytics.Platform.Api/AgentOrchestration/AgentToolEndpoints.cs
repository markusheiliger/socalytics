using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SocAlytics.Platform.Api.Http;
using SocAlytics.Platform.Api.Registry;
using SocAlytics.Platform.Api.Security;
using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.AgentOrchestration.Tools;
using SocAlytics.Platform.Application.Analysis.Evidence;
using SocAlytics.Platform.Domain.AgentOrchestration.Tools;

namespace SocAlytics.Platform.Api.AgentOrchestration;

internal sealed record AgentToolRequestDto(string? CallId, string? FencingToken, JsonElement? Arguments);

internal sealed record AgentToolResponseDto(string CallId, string ToolId, string Outcome, JsonNode? Result);

internal static partial class AgentToolEndpoints
{
	private const int MaxBodyBytes = 16 * 1024;

	private static readonly JsonSerializerOptions BodyOptions = new(JsonSerializerDefaults.Web);

	[GeneratedRegex("^[A-Za-z0-9._\\-]{1,128}$")]
	private static partial Regex CallIdPattern();

	public static IEndpointRouteBuilder MapAgentToolApi(this IEndpointRouteBuilder routes)
	{
		var group = routes.MapGroup("/api/v1/agent-tools")
			.RequireAuthorization(AuthorizationPolicyNames.AgentWorkload)
			.AddEndpointFilter<RequireHttpsEndpointFilter>()
			.AddEndpointFilter<JsonOnlyFilter>()
			.WithTags("Agent tools");

		MapTool(group, GetMatchToolHandler.ToolId, GetMatchAsync);
		MapTool(group, GetVideoClipTool.ToolId, GetVideoClipAsync);
		foreach (var entry in AgentToolAllowlist.Entries.Where(e => e.Answers == AgentToolAnswerKind.AnalysisUnavailable))
		{
			var toolId = entry.ToolId;
			MapTool(group, toolId, (attemptId, http, cancellationToken) =>
				UnavailableAnalysisAsync(toolId, attemptId, http, cancellationToken));
		}

		return routes;
	}

	private static void MapTool(
		IEndpointRouteBuilder group, string toolId,
		Func<Guid, HttpContext, CancellationToken, Task<IResult>> handler)
	{
		AgentToolAllowlist.TryGet(toolId, out var entry);
		group.MapPost($"/attempts/{{attemptId:guid}}/{entry.PathSegment}", handler)
			.WithName(entry.OperationId)
			.Accepts<AgentToolRequestDto>("application/json")
			.Produces<AgentToolResponseDto>()
			.ProducesProblem(StatusCodes.Status400BadRequest)
			.ProducesProblem(StatusCodes.Status401Unauthorized)
			.ProducesProblem(StatusCodes.Status403Forbidden)
			.ProducesProblem(StatusCodes.Status404NotFound)
			.ProducesProblem(StatusCodes.Status409Conflict)
			.ProducesProblem(StatusCodes.Status415UnsupportedMediaType)
			.ProducesProblem(StatusCodes.Status429TooManyRequests)
			.ProducesProblem(StatusCodes.Status503ServiceUnavailable);
	}

	private static async Task<IResult> GetMatchAsync(
		Guid attemptId, HttpContext http, CancellationToken cancellationToken)
	{
		var handler = http.RequestServices.GetRequiredService<GetMatchToolHandler>();
		var parsed = await ReadCallAsync(http, cancellationToken);
		if (parsed.Failure is not null)
		{
			return parsed.Failure;
		}

		var call = parsed.Call!;
		var result = await handler.HandleAsync(
			new GetMatchToolCommand(attemptId, call.ClientId, call.FencingToken, call.CallId, call.Arguments), cancellationToken);
		if (!result.IsSuccess)
		{
			return AgentWorkloadEndpoints.Failure(result.Failure);
		}

		return Answer(call.CallId, GetMatchToolHandler.ToolId, result.Value);
	}

	private static async Task<IResult> GetVideoClipAsync(
		Guid attemptId, HttpContext http, CancellationToken cancellationToken)
	{
		var authorize = http.RequestServices.GetRequiredService<AuthorizeToolCallHandler>();
		var tool = http.RequestServices.GetRequiredService<GetVideoClipTool>();
		var parsed = await ReadCallAsync(http, cancellationToken);
		if (parsed.Failure is not null)
		{
			return parsed.Failure;
		}

		var call = parsed.Call!;
		var authorized = await authorize.HandleAsync(
			new AuthorizeToolCallCommand(
				attemptId, call.ClientId, call.FencingToken, GetVideoClipTool.ToolId, call.CallId,
				ToolCallLineage.ArgumentsDigest(call.Arguments), true),
			cancellationToken);
		if (!authorized.IsSuccess)
		{
			return AgentWorkloadEndpoints.Failure(authorized.Failure);
		}

		var violations = new List<FieldViolation>();
		var (fact, range, retryFailed) = ParseClipArguments(call.Arguments, violations);
		var result = await tool.HandleAsync(
			new GetVideoClipToolCommand(authorized.Value, fact, range, retryFailed, violations), cancellationToken);
		return result.IsSuccess
			? Json(new AgentToolResponseDto(call.CallId, GetVideoClipTool.ToolId, "answered", result.Value.Result))
			: AgentWorkloadEndpoints.Failure(result.Failure);
	}

	private static async Task<IResult> UnavailableAnalysisAsync(
		string toolId, Guid attemptId, HttpContext http, CancellationToken cancellationToken)
	{
		var handler = http.RequestServices.GetRequiredService<UnavailableAnalysisToolHandler>();
		var parsed = await ReadCallAsync(http, cancellationToken);
		if (parsed.Failure is not null)
		{
			return parsed.Failure;
		}

		var call = parsed.Call!;
		var result = await handler.HandleAsync(
			new UnavailableAnalysisToolCommand(attemptId, call.ClientId, call.FencingToken, toolId, call.CallId, call.Arguments),
			cancellationToken);
		if (!result.IsSuccess)
		{
			return AgentWorkloadEndpoints.Failure(result.Failure);
		}

		var answer = result.Value;
		return Json(new AgentToolResponseDto(
			call.CallId, toolId, ToolCallNames.ToWireName(answer.Outcome), answer.Result));
	}

	private static IResult Answer(string callId, string toolId, ToolCallAnswer answer)
	{
		JsonNode? result = answer.Result;
		if (answer.Outcome == ToolCallOutcome.Unavailable)
		{
			AgentToolAllowlist.TryGet(toolId, out var entry);
			result = new JsonObject
			{
				["reason"] = answer.Reason is { } reason ? ToolCallNames.ToWireName(reason) : null,
				["producingCapability"] = entry?.ProducingCapability,
				["detail"] = answer.Reason == ToolCallReason.CapabilityNotInRun
					? "The requested capability is not part of the bound run."
					: "No accepted producer exists for this analysis.",
			};
		}

		return Json(new AgentToolResponseDto(callId, toolId, ToolCallNames.ToWireName(answer.Outcome), result));
	}

	private static IResult Json(AgentToolResponseDto dto) => new NoStoreJsonResult(dto);

	private sealed class NoStoreJsonResult(AgentToolResponseDto dto) : IResult
	{
		public Task ExecuteAsync(HttpContext httpContext)
		{
			httpContext.Response.Headers.CacheControl = "no-store";
			httpContext.Response.ContentType = "application/json";
			var json = new JsonObject
			{
				["callId"] = dto.CallId,
				["toolId"] = dto.ToolId,
				["outcome"] = dto.Outcome,
				["result"] = dto.Result?.DeepClone(),
			};
			return httpContext.Response.WriteAsync(json.ToJsonString(), httpContext.RequestAborted);
		}
	}

	private sealed record ToolCall(string ClientId, string CallId, string FencingToken, JsonNode? Arguments);

	private sealed record ParsedCall(ToolCall? Call, IResult? Failure);

	private static async Task<ParsedCall> ReadCallAsync(HttpContext http, CancellationToken cancellationToken)
	{
		http.Response.Headers.CacheControl = "no-store";
		if (http.User.FindFirst(AgentWorkloadClaimTypes.ClientId)?.Value is not { Length: > 0 } clientId)
		{
			return new ParsedCall(null, ProblemResults.From(OperationFailure.Unauthenticated()));
		}

		if (http.Request.ContentLength > MaxBodyBytes)
		{
			return new ParsedCall(null, ProblemResults.Problem(StatusCodes.Status413PayloadTooLarge, "payload-too-large"));
		}

		JsonObject? body;
		try
		{
			using var buffer = new MemoryStream();
			var chunk = new byte[4096];
			int read;
			while ((read = await http.Request.Body.ReadAsync(chunk, cancellationToken)) > 0)
			{
				buffer.Write(chunk, 0, read);
				if (buffer.Length > MaxBodyBytes)
				{
					return new ParsedCall(null, ProblemResults.Problem(StatusCodes.Status413PayloadTooLarge, "payload-too-large"));
				}
			}

			buffer.Position = 0;
			body = await JsonSerializer.DeserializeAsync<JsonObject>(buffer, BodyOptions, cancellationToken);
		}
		catch (JsonException)
		{
			body = null;
		}

		if (body is null)
		{
			return Invalid(new FieldViolation("body", "invalid"));
		}

		var violations = new List<FieldViolation>();
		violations.AddRange(body.Where(p => p.Key is not ("callId" or "fencingToken" or "arguments"))
			.Select(p => new FieldViolation(p.Key, "unknown")));

		var callId = Text(body, "callId", violations);
		if (callId is null || !CallIdPattern().IsMatch(callId))
		{
			violations.Add(new FieldViolation("callId", "invalid"));
		}

		var fencingToken = Text(body, "fencingToken", violations);
		if (string.IsNullOrWhiteSpace(fencingToken) || fencingToken.Length > 255)
		{
			violations.Add(new FieldViolation("fencingToken", "invalid"));
		}

		var arguments = body["arguments"];
		if (arguments is not null and not JsonObject)
		{
			violations.Add(new FieldViolation("arguments", "invalid"));
		}

		if (violations.Count > 0)
		{
			return new ParsedCall(null, ProblemResults.From(OperationFailure.Validation([.. violations.DistinctBy(v => v.Field)])));
		}

		return new ParsedCall(new ToolCall(clientId, callId!, fencingToken!, arguments), null);
	}

	private static ParsedCall Invalid(FieldViolation violation) =>
		new(null, ProblemResults.From(OperationFailure.Validation(violation)));

	private static string? Text(JsonObject body, string name, List<FieldViolation> violations)
	{
		if (body[name] is JsonValue value && value.TryGetValue<string>(out var text))
		{
			return text;
		}

		if (body[name] is not null)
		{
			violations.Add(new FieldViolation(name, "invalid"));
		}

		return null;
	}

	private static (EvidenceFactRequest? Fact, EvidenceRangeRequest? Range, bool RetryFailed) ParseClipArguments(
		JsonNode? arguments, List<FieldViolation> violations)
	{
		if (arguments is not JsonObject root)
		{
			violations.Add(new FieldViolation("arguments", "invalid"));
			return (null, null, false);
		}

		violations.AddRange(root.Where(p => p.Key is not ("fact" or "range" or "retryFailed"))
			.Select(p => new FieldViolation($"arguments.{p.Key}", "unknown")));

		EvidenceFactRequest? fact = null;
		if (root["fact"] is { } factNode)
		{
			if (factNode is not JsonObject factObject)
			{
				violations.Add(new FieldViolation("fact", "invalid"));
			}
			else
			{
				violations.AddRange(factObject
					.Where(p => p.Key is not ("acceptedResultId" or "ordinal" or "contextBeforeMs" or "contextAfterMs"))
					.Select(p => new FieldViolation($"fact.{p.Key}", "unknown")));
				Guid? id = null;
				if (factObject["acceptedResultId"] is { } idNode)
				{
					if (idNode is JsonValue idValue && idValue.TryGetValue<string>(out var idText)
						&& Guid.TryParse(idText, CultureInfo.InvariantCulture, out var parsedId))
					{
						id = parsedId;
					}
					else
					{
						violations.Add(new FieldViolation("fact.acceptedResultId", "invalid"));
					}
				}

				var ordinal = Number(factObject, "ordinal", "fact.ordinal", violations);
				if (ordinal is > int.MaxValue or < int.MinValue)
				{
					violations.Add(new FieldViolation("fact.ordinal", "invalid"));
					ordinal = null;
				}

				fact = new EvidenceFactRequest(
					id, (int?)ordinal, Number(factObject, "contextBeforeMs", "fact.contextBeforeMs", violations),
					Number(factObject, "contextAfterMs", "fact.contextAfterMs", violations));
			}
		}

		EvidenceRangeRequest? range = null;
		if (root["range"] is { } rangeNode)
		{
			if (rangeNode is not JsonObject rangeObject)
			{
				violations.Add(new FieldViolation("range", "invalid"));
			}
			else
			{
				violations.AddRange(rangeObject.Where(p => p.Key is not ("fromMs" or "toMs"))
					.Select(p => new FieldViolation($"range.{p.Key}", "unknown")));
				range = new EvidenceRangeRequest(
					Number(rangeObject, "fromMs", "range.fromMs", violations), Number(rangeObject, "toMs", "range.toMs", violations));
			}
		}

		var retryFailed = false;
		if (root["retryFailed"] is { } retryNode)
		{
			if (retryNode is JsonValue retryValue && retryValue.TryGetValue<bool>(out var flag))
			{
				retryFailed = flag;
			}
			else
			{
				violations.Add(new FieldViolation("retryFailed", "invalid"));
			}
		}

		return (fact, range, retryFailed);
	}

	private static long? Number(JsonObject node, string name, string field, List<FieldViolation> violations)
	{
		if (node[name] is not { } value)
		{
			return null;
		}

		if (value is JsonValue json && json.TryGetValue<long>(out var number))
		{
			return number;
		}

		violations.Add(new FieldViolation(field, "invalid"));
		return null;
	}
}
