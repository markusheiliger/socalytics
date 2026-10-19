using System.Text.Json;
using SocAlytics.Platform.Api.Http;
using SocAlytics.Platform.Api.Security;
using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.AgentOrchestration.Catalog;
using SocAlytics.Platform.Application.AgentOrchestration.Catalog.Lifecycle;
using SocAlytics.Platform.Application.AgentOrchestration.Catalog.Reads;
using SocAlytics.Platform.Application.AgentOrchestration.Catalog.Registration;
using SocAlytics.Platform.Domain.AgentOrchestration.Catalog;

namespace SocAlytics.Platform.Api.AgentOrchestration;

internal sealed record CatalogBaselineAgentDto(string AgentId, string Group, string Domain, string DisplayName, IReadOnlyList<string> PermittedTools);

internal sealed record CatalogBaselineDto(
	string SchemaVersion, string Kind, string Version, IReadOnlyList<string> ToolAllowlist, IReadOnlyList<CatalogBaselineAgentDto> Agents);

internal sealed record AgentCatalogArtifactDto(
	string Kind, string Id, string Version, string Digest, DateTimeOffset RegisteredAt, JsonElement Document);

internal sealed record AgentCatalogArtifactSummaryDto(
	string Kind, string Id, string Version, string Digest, string RegisteredBy, DateTimeOffset RegisteredAt);

internal sealed record AgentCatalogArtifactListDto(IReadOnlyList<AgentCatalogArtifactSummaryDto> Items);

internal sealed record AgentVersionDto(
	string AgentId, string Version, string Digest, string Group, string State, bool Enabled, JsonElement Document,
	string RegisteredBy, DateTimeOffset RegisteredAt, string? ApprovedBy, DateTimeOffset? ApprovedAt, bool? SelfApproval,
	bool? DifferentiationAcknowledged, string? RetiredBy, DateTimeOffset? RetiredAt);

internal sealed record AgentVersionSummaryDto(
	string AgentId, string Version, string Digest, string Group, string State, bool Enabled, DateTimeOffset RegisteredAt);

internal sealed record AgentVersionListDto(IReadOnlyList<AgentVersionSummaryDto> Items);

internal sealed record ApproveAgentVersionRequestDto(bool? DifferentiationReviewAcknowledged);

internal sealed record AgentCatalogViolationDto(string Code, string Pointer, string Detail);

internal static class AgentCatalogEndpoints
{
	private static readonly JsonSerializerOptions BodyOptions = new(JsonSerializerDefaults.Web)
	{
		UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
	};

	public static IEndpointRouteBuilder MapAgentCatalogEndpoints(this IEndpointRouteBuilder routes)
	{
		var members = routes.MapMemberApi();
		members.MapGet("/agent-catalog/baseline", GetBaselineAsync)
			.WithName("getAgentCatalogBaseline")
			.WithTags("Agent catalog")
			.Produces<CatalogBaselineDto>()
			.ProducesProblem(StatusCodes.Status403Forbidden);
		members.MapPost("/agent-catalog/artifacts/{kind}", RegisterArtifactAsync)
			.AddEndpointFilter<JsonOnlyFilter>()
			.WithName("registerAgentCatalogArtifact")
			.WithTags("Agent catalog")
			.Accepts<JsonElement>("application/json")
			.Produces<AgentCatalogArtifactDto>(StatusCodes.Status201Created)
			.Produces<AgentCatalogArtifactDto>()
			.ProducesProblem(StatusCodes.Status400BadRequest)
			.ProducesProblem(StatusCodes.Status403Forbidden)
			.ProducesProblem(StatusCodes.Status404NotFound)
			.ProducesProblem(StatusCodes.Status409Conflict);
		members.MapGet("/agent-catalog/artifacts/{kind}", ListArtifactsAsync)
			.WithName("listAgentCatalogArtifacts")
			.WithTags("Agent catalog")
			.Produces<AgentCatalogArtifactListDto>()
			.ProducesProblem(StatusCodes.Status403Forbidden)
			.ProducesProblem(StatusCodes.Status404NotFound);
		members.MapGet("/agent-catalog/artifacts/{kind}/{id}/versions/{version}", GetArtifactAsync)
			.WithName("getAgentCatalogArtifact")
			.WithTags("Agent catalog")
			.Produces<AgentCatalogArtifactDto>()
			.Produces(StatusCodes.Status304NotModified)
			.ProducesProblem(StatusCodes.Status403Forbidden)
			.ProducesProblem(StatusCodes.Status404NotFound);
		members.MapPost("/agent-catalog/agent-versions", RegisterAgentVersionAsync)
			.AddEndpointFilter<JsonOnlyFilter>()
			.WithName("registerAgentVersion")
			.WithTags("Agent catalog")
			.Accepts<JsonElement>("application/json")
			.Produces<AgentVersionDto>(StatusCodes.Status201Created)
			.Produces<AgentVersionDto>()
			.ProducesProblem(StatusCodes.Status400BadRequest)
			.ProducesProblem(StatusCodes.Status403Forbidden)
			.ProducesProblem(StatusCodes.Status409Conflict);
		members.MapGet("/agent-catalog/agent-versions", ListAgentVersionsAsync)
			.WithName("listAgentVersions")
			.WithTags("Agent catalog")
			.Produces<AgentVersionListDto>()
			.ProducesProblem(StatusCodes.Status400BadRequest)
			.ProducesProblem(StatusCodes.Status403Forbidden);
		members.MapGet("/agent-catalog/agent-versions/{agentId}/versions/{version}", GetAgentVersionAsync)
			.WithName("getAgentVersion")
			.WithTags("Agent catalog")
			.Produces<AgentVersionDto>()
			.Produces(StatusCodes.Status304NotModified)
			.ProducesProblem(StatusCodes.Status403Forbidden)
			.ProducesProblem(StatusCodes.Status404NotFound);
		members.MapPost("/agent-catalog/agent-versions/{agentId}/versions/{version}/approval", ApproveAsync)
			.WithName("approveAgentVersion")
			.WithTags("Agent catalog")
			.Accepts<ApproveAgentVersionRequestDto>("application/json")
			.Produces<AgentVersionDto>()
			.ProducesProblem(StatusCodes.Status400BadRequest)
			.ProducesProblem(StatusCodes.Status403Forbidden)
			.ProducesProblem(StatusCodes.Status404NotFound)
			.ProducesProblem(StatusCodes.Status409Conflict)
			.ProducesProblem(StatusCodes.Status412PreconditionFailed)
			.ProducesProblem(StatusCodes.Status428PreconditionRequired);
		members.MapPost("/agent-catalog/agent-versions/{agentId}/versions/{version}/retirement", RetireAsync)
			.WithName("retireAgentVersion")
			.WithTags("Agent catalog")
			.Produces<AgentVersionDto>()
			.ProducesProblem(StatusCodes.Status403Forbidden)
			.ProducesProblem(StatusCodes.Status404NotFound)
			.ProducesProblem(StatusCodes.Status409Conflict)
			.ProducesProblem(StatusCodes.Status412PreconditionFailed)
			.ProducesProblem(StatusCodes.Status428PreconditionRequired);
		return routes;
	}

	private static async Task<IResult> GetBaselineAsync(GetAgentCatalogBaselineHandler handler, CancellationToken cancellationToken)
	{
		var result = await handler.HandleAsync(cancellationToken);
		if (!result.IsSuccess)
		{
			return ProblemResults.From(result.Failure);
		}

		var b = result.Value;
		return Results.Ok(new CatalogBaselineDto(
			b.SchemaVersion, b.Kind, b.Version, b.ToolAllowlist,
			[.. b.Agents.Select(a => new CatalogBaselineAgentDto(a.AgentId, a.Group, a.Domain, a.DisplayName, a.PermittedTools))]));
	}

	private static async Task<IResult> RegisterArtifactAsync(
		string kind, HttpContext http, RegisterAgentCatalogArtifactHandler handler, CancellationToken cancellationToken)
	{
		if (!TryParseKind(kind, out var parsedKind))
		{
			return ProblemResults.From(OperationFailure.NotFound());
		}

		var document = await ReadDocumentAsync(http, cancellationToken);
		if (document is null)
		{
			return Malformed();
		}

		var result = await handler.HandleAsync(new RegisterAgentCatalogArtifactCommand(parsedKind, document.Value), cancellationToken);
		if (!result.IsRegistered)
		{
			return Refusal(result);
		}

		var record = result.Record!;
		var view = AgentCatalogViews.Of(record);
		var dto = ToDto(view);
		http.Response.Headers.ETag = ArtifactEtag(view.Digest);
		return result.Created
			? Results.Created($"/api/v1/agent-catalog/artifacts/{kind}/{Uri.EscapeDataString(view.Id)}/versions/{Uri.EscapeDataString(view.Version)}", dto)
			: Results.Ok(dto);
	}

	private static async Task<IResult> ListArtifactsAsync(
		string kind, string? id, ListAgentCatalogArtifactsHandler handler, CancellationToken cancellationToken)
	{
		if (!TryParseKind(kind, out var parsedKind))
		{
			return ProblemResults.From(OperationFailure.NotFound());
		}

		var result = await handler.HandleAsync(parsedKind, id, cancellationToken);
		return result.IsSuccess
			? Results.Ok(new AgentCatalogArtifactListDto([.. result.Value.Select(v =>
				new AgentCatalogArtifactSummaryDto(v.Kind, v.Id, v.Version, v.Digest, v.RegisteredBy, v.RegisteredAt))]))
			: ProblemResults.From(result.Failure);
	}

	private static async Task<IResult> GetArtifactAsync(
		string kind, string id, string version, HttpContext http, GetAgentCatalogArtifactHandler handler, CancellationToken cancellationToken)
	{
		if (!TryParseKind(kind, out var parsedKind))
		{
			return ProblemResults.From(OperationFailure.NotFound());
		}

		var result = await handler.HandleAsync(parsedKind, id, version, cancellationToken);
		if (!result.IsSuccess)
		{
			return ProblemResults.From(result.Failure);
		}

		// The record is immutable and content-addressed, so its digest is its validator.
		return Conditional(http, ArtifactEtag(result.Value.Digest), () => ToDto(result.Value));
	}

	private static async Task<IResult> RegisterAgentVersionAsync(
		string? version, HttpContext http, RegisterAgentVersionHandler handler, CancellationToken cancellationToken)
	{
		var document = await ReadDocumentAsync(http, cancellationToken);
		if (document is null)
		{
			return Malformed();
		}

		var result = await handler.HandleAsync(new RegisterAgentVersionCommand(version, document.Value), cancellationToken);
		if (!result.IsRegistered)
		{
			return Refusal(result);
		}

		var view = AgentCatalogViews.Of(result.Record!, false);
		http.Response.Headers.ETag = IfMatchHeader.Format(view.RowVersion);
		return result.Created
			? Results.Created($"/api/v1/agent-catalog/agent-versions/{Uri.EscapeDataString(view.AgentId)}/versions/{Uri.EscapeDataString(view.Version)}", ToDto(view))
			: Results.Ok(ToDto(view));
	}

	private static async Task<IResult> ListAgentVersionsAsync(
		string? agentId, string? state, ListAgentVersionsHandler handler, CancellationToken cancellationToken)
	{
		var result = await handler.HandleAsync(agentId, state, cancellationToken);
		return result.IsSuccess
			? Results.Ok(new AgentVersionListDto([.. result.Value.Select(v =>
				new AgentVersionSummaryDto(v.AgentId, v.Version, v.Digest, v.Group, v.State, v.Enabled, v.RegisteredAt))]))
			: ProblemResults.From(result.Failure);
	}

	private static async Task<IResult> GetAgentVersionAsync(
		string agentId, string version, HttpContext http, GetAgentVersionHandler handler, CancellationToken cancellationToken)
	{
		var result = await handler.HandleAsync(agentId, version, cancellationToken);
		return result.IsSuccess
			? Conditional(http, IfMatchHeader.Format(result.Value.RowVersion), () => ToDto(result.Value))
			: ProblemResults.From(result.Failure);
	}

	private static async Task<IResult> ApproveAsync(
		string agentId, string version, HttpContext http, ApproveAgentVersionHandler handler, CancellationToken cancellationToken)
	{
		var expected = IfMatchHeader.Parse(http.Request);
		if (!expected.IsSuccess)
		{
			return ProblemResults.From(expected.Failure);
		}

		ApproveAgentVersionRequestDto? body = null;
		if (http.Request.ContentLength is > 0 || http.Request.Headers.ContainsKey("Transfer-Encoding"))
		{
			try
			{
				body = await JsonSerializer.DeserializeAsync<ApproveAgentVersionRequestDto>(http.Request.Body, BodyOptions, cancellationToken);
			}
			catch (JsonException)
			{
				return ProblemResults.From(OperationFailure.Validation(new FieldViolation("body", "invalid")));
			}
		}

		var result = await handler.HandleAsync(
			new ApproveAgentVersionCommand(agentId, version, expected.Value, body?.DifferentiationReviewAcknowledged ?? false), cancellationToken);
		return Versioned(http, result);
	}

	private static async Task<IResult> RetireAsync(
		string agentId, string version, HttpContext http, RetireAgentVersionHandler handler, CancellationToken cancellationToken)
	{
		var expected = IfMatchHeader.Parse(http.Request);
		if (!expected.IsSuccess)
		{
			return ProblemResults.From(expected.Failure);
		}

		var result = await handler.HandleAsync(new AgentVersionTransitionCommand(agentId, version, expected.Value), cancellationToken);
		return Versioned(http, result);
	}

	private static IResult Versioned(HttpContext http, OperationResult<AgentVersionView> result)
	{
		if (!result.IsSuccess)
		{
			return ProblemResults.From(result.Failure);
		}

		http.Response.Headers.ETag = IfMatchHeader.Format(result.Value.RowVersion);
		return Results.Ok(ToDto(result.Value));
	}

	private static IResult Refusal<T>(RegisterCatalogResult<T> result)
		where T : class
	{
		if (result.Failure is { } failure)
		{
			return ProblemResults.From(failure);
		}

		return ProblemResults.Problem(
			StatusCodes.Status400BadRequest,
			AgentCatalogProblems.ValidationFailed,
			new Dictionary<string, object?>
			{
				[AgentCatalogProblems.ViolationsExtension] =
					result.Violations.Select(v => new AgentCatalogViolationDto(v.Code, v.Pointer, v.Detail)).ToArray(),
			});
	}

	private static IResult Malformed() =>
		ProblemResults.Problem(
			StatusCodes.Status400BadRequest,
			AgentCatalogProblems.ValidationFailed,
			new Dictionary<string, object?>
			{
				[AgentCatalogProblems.ViolationsExtension] = new[]
				{
					new AgentCatalogViolationDto(AgentCatalogViolationCodes.SchemaViolation, "", "The request body is not a JSON document."),
				},
			});

	private static bool TryParseKind(string kind, out AgentCatalogRecordKind parsed) =>
		kind.EndsWith('s') && AgentCatalogRecordKinds.TryParse(kind[..^1], out parsed)
			? true
			: Fail(out parsed);

	private static bool Fail(out AgentCatalogRecordKind parsed)
	{
		parsed = default;
		return false;
	}

	private static AgentCatalogArtifactDto ToDto(AgentCatalogArtifactView v) =>
		new(v.Kind, v.Id, v.Version, v.Digest, v.RegisteredAt, ParseDocument(v.Document));

	private static AgentVersionDto ToDto(AgentVersionView v) =>
		new(v.AgentId, v.Version, v.Digest, v.Group, v.State, v.Enabled, ParseDocument(v.Document), v.RegisteredBy, v.RegisteredAt,
			v.ApprovedBy, v.ApprovedAt, v.SelfApproval, v.DifferentiationAcknowledged, v.RetiredBy, v.RetiredAt);

	private static JsonElement ParseDocument(string canonical)
	{
		using var parsed = JsonDocument.Parse(canonical);
		return parsed.RootElement.Clone();
	}

	private static string ArtifactEtag(string digest) => $"\"{digest}\"";

	private static async Task<JsonElement?> ReadDocumentAsync(HttpContext http, CancellationToken cancellationToken)
	{
		try
		{
			using var parsed = await JsonDocument.ParseAsync(http.Request.Body, cancellationToken: cancellationToken);
			return parsed.RootElement.Clone();
		}
		catch (JsonException)
		{
			return null;
		}
	}

	private static IResult Conditional<T>(HttpContext http, string etag, Func<T> body)
	{
		http.Response.Headers.ETag = etag;
		var candidates = http.Request.Headers.IfNoneMatch.ToString()
			.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
		return candidates.Any(c => c == "*" || c == etag)
			? Results.StatusCode(StatusCodes.Status304NotModified)
			: Results.Ok(body());
	}
}
