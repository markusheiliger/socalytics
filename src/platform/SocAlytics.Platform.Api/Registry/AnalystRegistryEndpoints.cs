using System.Text.Json;
using System.Text.Json.Nodes;
using SocAlytics.Platform.Api.Http;
using SocAlytics.Platform.Api.Security;
using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.Registry;
using SocAlytics.Platform.Application.Registry.Images;
using SocAlytics.Platform.Application.Registry.Profiles;
using SocAlytics.Platform.Application.Registry.Workflows;
using SocAlytics.Platform.Domain.Analysis;
using SocAlytics.Platform.Domain.Registry;

namespace SocAlytics.Platform.Api.Registry;

internal static class AnalystRegistryEndpoints
{
	private const string ImagesPath = MemberApiRouteGroup.Prefix + "/registry/analyst-images";
	private const string ProfilesPath = MemberApiRouteGroup.Prefix + "/registry/analyst-profiles";
	private const string WorkflowsPath = MemberApiRouteGroup.Prefix + "/registry/workflows";

	public static IEndpointRouteBuilder MapAnalystRegistryEndpoints(this IEndpointRouteBuilder routes)
	{
		var group = routes.MapMemberApi().WithTags("Analyst registry");
		group.MapPost("/registry/analyst-images", RegisterImageAsync)
			.AddEndpointFilter<JsonOnlyFilter>()
			.WithName("registerAnalystImage")
			.Accepts<RegisterAnalystImageRequest>("application/json")
			.Produces<AnalystImageDto>(StatusCodes.Status201Created)
			.Produces<AnalystImageDto>()
			.ProducesProblem(StatusCodes.Status400BadRequest)
			.ProducesProblem(StatusCodes.Status401Unauthorized)
			.ProducesProblem(StatusCodes.Status403Forbidden)
			.ProducesProblem(StatusCodes.Status422UnprocessableEntity)
			.ProducesProblem(StatusCodes.Status502BadGateway)
			.ProducesProblem(StatusCodes.Status503ServiceUnavailable);
		group.MapGet("/registry/analyst-images", ListImagesAsync)
			.WithName("listAnalystImages")
			.Produces<AnalystImageListDto>()
			.ProducesProblem(StatusCodes.Status400BadRequest)
			.ProducesProblem(StatusCodes.Status401Unauthorized)
			.ProducesProblem(StatusCodes.Status403Forbidden);
		group.MapGet("/registry/analyst-images/{digest}", GetImageAsync)
			.WithName("getAnalystImage")
			.Produces<AnalystImageDto>()
			.ProducesProblem(StatusCodes.Status400BadRequest)
			.ProducesProblem(StatusCodes.Status401Unauthorized)
			.ProducesProblem(StatusCodes.Status403Forbidden)
			.ProducesProblem(StatusCodes.Status404NotFound);
		group.MapPost("/registry/analyst-profiles", CreateProfileAsync)
			.AddEndpointFilter<JsonOnlyFilter>()
			.WithName("createAnalystProfile")
			.Accepts<CreateAnalystProfileRequest>("application/json")
			.Produces<AnalystProfileDto>(StatusCodes.Status201Created)
			.Produces<AnalystProfileDto>()
			.ProducesProblem(StatusCodes.Status400BadRequest)
			.ProducesProblem(StatusCodes.Status401Unauthorized)
			.ProducesProblem(StatusCodes.Status403Forbidden)
			.ProducesProblem(StatusCodes.Status404NotFound)
			.ProducesProblem(StatusCodes.Status409Conflict)
			.ProducesProblem(StatusCodes.Status422UnprocessableEntity);
		group.MapGet("/registry/analyst-profiles", ListProfilesAsync)
			.WithName("listAnalystProfiles")
			.Produces<AnalystProfileListDto>()
			.ProducesProblem(StatusCodes.Status400BadRequest)
			.ProducesProblem(StatusCodes.Status401Unauthorized)
			.ProducesProblem(StatusCodes.Status403Forbidden);
		group.MapGet("/registry/analyst-profiles/{profileId}/versions/{version:int}", GetProfileAsync)
			.WithName("getAnalystProfile")
			.Produces<AnalystProfileDto>()
			.ProducesProblem(StatusCodes.Status400BadRequest)
			.ProducesProblem(StatusCodes.Status401Unauthorized)
			.ProducesProblem(StatusCodes.Status403Forbidden)
			.ProducesProblem(StatusCodes.Status404NotFound);
		group.MapPost("/registry/analyst-profiles/{profileId}/versions/{version:int}/approval", ApproveProfileAsync)
			.WithName("approveAnalystProfile")
			.Produces<AnalystProfileDto>()
			.ProducesProblem(StatusCodes.Status400BadRequest)
			.ProducesProblem(StatusCodes.Status401Unauthorized)
			.ProducesProblem(StatusCodes.Status403Forbidden)
			.ProducesProblem(StatusCodes.Status404NotFound)
			.ProducesProblem(StatusCodes.Status409Conflict)
			.ProducesProblem(StatusCodes.Status412PreconditionFailed)
			.ProducesProblem(StatusCodes.Status428PreconditionRequired);
		group.MapPost("/registry/analyst-profiles/{profileId}/versions/{version:int}/retirement", RetireProfileAsync)
			.WithName("retireAnalystProfile")
			.Produces<AnalystProfileDto>()
			.ProducesProblem(StatusCodes.Status400BadRequest)
			.ProducesProblem(StatusCodes.Status401Unauthorized)
			.ProducesProblem(StatusCodes.Status403Forbidden)
			.ProducesProblem(StatusCodes.Status404NotFound)
			.ProducesProblem(StatusCodes.Status409Conflict)
			.ProducesProblem(StatusCodes.Status412PreconditionFailed)
			.ProducesProblem(StatusCodes.Status428PreconditionRequired);
		group.MapPost("/registry/workflows", DefineWorkflowAsync)
			.AddEndpointFilter<JsonOnlyFilter>()
			.WithName("defineWorkflow")
			.Accepts<DefineWorkflowRequest>("application/json")
			.Produces<RegistryWorkflowDefinitionDto>(StatusCodes.Status201Created)
			.ProducesProblem(StatusCodes.Status400BadRequest)
			.ProducesProblem(StatusCodes.Status401Unauthorized)
			.ProducesProblem(StatusCodes.Status403Forbidden)
			.ProducesProblem(StatusCodes.Status409Conflict)
			.ProducesProblem(StatusCodes.Status422UnprocessableEntity);
		group.MapGet("/registry/workflows", ListWorkflowsAsync)
			.WithName("listWorkflows")
			.Produces<WorkflowListDto>()
			.ProducesProblem(StatusCodes.Status400BadRequest)
			.ProducesProblem(StatusCodes.Status401Unauthorized)
			.ProducesProblem(StatusCodes.Status403Forbidden);
		group.MapGet("/registry/workflows/{workflowId}/versions/{version}", GetWorkflowAsync)
			.WithName("getWorkflow")
			.Produces<RegistryWorkflowDefinitionDto>()
			.ProducesProblem(StatusCodes.Status400BadRequest)
			.ProducesProblem(StatusCodes.Status401Unauthorized)
			.ProducesProblem(StatusCodes.Status403Forbidden)
			.ProducesProblem(StatusCodes.Status404NotFound);
		group.MapGet("/registry/default-workflow", GetDefaultWorkflowAsync)
			.WithName("getDefaultWorkflow")
			.Produces<DefaultWorkflowDto>()
			.ProducesProblem(StatusCodes.Status400BadRequest)
			.ProducesProblem(StatusCodes.Status401Unauthorized)
			.ProducesProblem(StatusCodes.Status403Forbidden)
			.ProducesProblem(StatusCodes.Status404NotFound);
		group.MapPut("/registry/default-workflow", SetDefaultWorkflowAsync)
			.AddEndpointFilter<JsonOnlyFilter>()
			.WithName("setDefaultWorkflow")
			.Accepts<SetDefaultWorkflowRequest>("application/json")
			.Produces<DefaultWorkflowDto>()
			.ProducesProblem(StatusCodes.Status400BadRequest)
			.ProducesProblem(StatusCodes.Status401Unauthorized)
			.ProducesProblem(StatusCodes.Status403Forbidden)
			.ProducesProblem(StatusCodes.Status412PreconditionFailed)
			.ProducesProblem(StatusCodes.Status422UnprocessableEntity)
			.ProducesProblem(StatusCodes.Status428PreconditionRequired);
		return routes;
	}

	internal static AnalystImageDto ToDto(StoredCapabilityDeclaration s)
	{
		var d = s.Declaration;
		return new AnalystImageDto(
			d.ImageDigest,
			s.Repository,
			s.ManifestDigest,
			new AnalystImageCapabilityDto(
				d.CapabilityId,
				d.CapabilityVersion,
				AnalysisPersistedNames.ToPersistedName(d.Tier),
				AnalysisPersistedNames.ToPersistedName(d.ExecutionScope)),
			d.ModelDigest,
			JsonNode.Parse(s.ManifestJson),
			s.Source,
			s.RegisteredAt,
			s.RegisteredBy);
	}

	private static int StatusOf(string code) => code switch
	{
		ImageImportRefusalCodes.ReferenceNotDigest or ImageImportRefusalCodes.ReferenceNotAllowed => StatusCodes.Status400BadRequest,
		ImageImportRefusalCodes.ImageNotFound => StatusCodes.Status404NotFound,
		ImageImportRefusalCodes.RegistryUnavailable => StatusCodes.Status503ServiceUnavailable,
		ImageImportRefusalCodes.DigestMismatch => StatusCodes.Status502BadGateway,
		_ => StatusCodes.Status422UnprocessableEntity,
	};

	private static async Task<IResult> RegisterImageAsync(
		HttpContext http, RegisterAnalystImageHandler handler, CancellationToken cancellationToken)
	{
		RegisterAnalystImageRequest? body;
		try
		{
			body = await http.Request.ReadFromJsonAsync<RegisterAnalystImageRequest>(cancellationToken);
		}
		catch (JsonException)
		{
			body = null;
		}

		var result = await handler.HandleAsync(new RegisterAnalystImageCommand(body?.Reference), cancellationToken);
		if (result.Failure is { } failure)
		{
			return ProblemResults.From(failure);
		}

		if (!result.IsRegistered)
		{
			return ProblemResults.Problem(StatusOf(result.RefusalCode!), result.RefusalCode!, result.Details);
		}

		var declaration = result.Declaration!;
		var location = $"{ImagesPath}/{Uri.EscapeDataString(declaration.Declaration.ImageDigest)}";
		http.Response.Headers.Location = location;
		return Results.Json(ToDto(declaration), statusCode: result.Created ? StatusCodes.Status201Created : StatusCodes.Status200OK);
	}

	private static async Task<IResult> ListImagesAsync(
		string? capabilityId, ListAnalystImagesHandler handler, CancellationToken cancellationToken)
	{
		var result = await handler.HandleAsync(capabilityId, cancellationToken);
		return result.IsSuccess
			? Results.Json(new AnalystImageListDto(result.Value.Select(s => new AnalystImageListItemDto(
				s.Declaration.ImageDigest,
				s.Declaration.CapabilityId,
				s.Declaration.CapabilityVersion,
				s.RegisteredAt)).ToArray()))
			: ProblemResults.From(result.Failure);
	}

	private static async Task<IResult> GetImageAsync(string digest, GetAnalystImageHandler handler, CancellationToken cancellationToken)
	{
		var result = await handler.HandleAsync(digest, cancellationToken);
		return result.IsSuccess ? Results.Json(ToDto(result.Value)) : ProblemResults.From(result.Failure);
	}

	internal static AnalystProfileDto ToDto(StoredAnalystProfile s)
	{
		var p = s.Profile;
		var c = p.Content;
		return new AnalystProfileDto(
			p.ProfileId,
			p.Version,
			p.Digest.ToString(),
			c.ImageDigest,
			p.State.ToPersistedName(),
			new AnalystProfileContentDto(
				c.CapabilityId,
				c.CapabilityVersion,
				c.ModelDigest,
				c.RuntimeConstraints,
				c.Accelerators,
				c.Resources,
				c.MaxAttempts,
				c.LeaseSeconds,
				c.StaleTimeoutSeconds,
				c.ExecutionTimeoutSeconds,
				c.VideoPolicy,
				c.InputSchemaVersions,
				c.OutputSchemaVersion),
			p.CreatedAt,
			p.CreatedBy,
			p.ApprovedAt,
			p.ApprovedBy,
			p.RetiredAt,
			p.RetiredBy);
	}

	private static IResult ProfileResult(HttpContext http, OperationResult<StoredAnalystProfile> result)
	{
		if (!result.IsSuccess)
		{
			return ProblemResults.From(result.Failure);
		}

		http.Response.Headers.ETag = IfMatchHeader.Format(result.Value.Version);
		return Results.Json(ToDto(result.Value));
	}

	private static async Task<IResult> CreateProfileAsync(HttpContext http, CreateAnalystProfileHandler handler, CancellationToken cancellationToken)
	{
		CreateAnalystProfileRequest? body;
		try
		{
			body = await http.Request.ReadFromJsonAsync<CreateAnalystProfileRequest>(cancellationToken);
		}
		catch (JsonException)
		{
			body = null;
		}

		var c = body?.Content;
		var content = c is null
			? null
			: new AnalystProfileContent(
				c.CapabilityId ?? "", c.CapabilityVersion ?? "", body!.ImageDigest ?? "", c.ModelDigest ?? "",
				c.RuntimeConstraints ?? [], c.Accelerators ?? [], c.Resources ?? [], c.MaxAttempts, c.LeaseSeconds,
				c.StaleTimeoutSeconds, c.ExecutionTimeoutSeconds, c.VideoPolicy ?? "", c.InputSchemaVersions ?? [],
				c.OutputSchemaVersion ?? "");
		var result = await handler.HandleAsync(new CreateAnalystProfileCommand(body?.ProfileId, body?.ImageDigest, content), cancellationToken);
		if (result.Failure is { } failure)
		{
			return ProblemResults.From(failure);
		}

		if (result.Profile is null)
		{
			return ProblemResults.Problem(StatusCodes.Status422UnprocessableEntity, CreateAnalystProfileHandler.ProfileContradictsDeclaration, result.Contradictions);
		}

		var profile = result.Profile;
		http.Response.Headers.ETag = IfMatchHeader.Format(profile.Version);
		http.Response.Headers.Location =
			$"{ProfilesPath}/{Uri.EscapeDataString(profile.Profile.ProfileId)}/versions/{profile.Profile.Version}";
		return Results.Json(ToDto(profile), statusCode: result.Created ? StatusCodes.Status201Created : StatusCodes.Status200OK);
	}

	private static async Task<IResult> ListProfilesAsync(
		string? state, string? capabilityId, ListAnalystProfilesHandler handler, CancellationToken cancellationToken)
	{
		var result = await handler.HandleAsync(state, capabilityId, cancellationToken);
		return result.IsSuccess
			? Results.Json(new AnalystProfileListDto(result.Value.Select(ToDto).ToArray()))
			: ProblemResults.From(result.Failure);
	}

	private static async Task<IResult> GetProfileAsync(
		string profileId, int version, HttpContext http, GetAnalystProfileHandler handler, CancellationToken cancellationToken) =>
		ProfileResult(http, await handler.HandleAsync(profileId, version, cancellationToken));

	private static async Task<IResult> ApproveProfileAsync(
		string profileId, int version, HttpContext http, ApproveAnalystProfileHandler handler, CancellationToken cancellationToken)
	{
		var expected = IfMatchHeader.Parse(http.Request);
		return expected.IsSuccess
			? ProfileResult(http, await handler.HandleAsync(new ProfileTransitionCommand(profileId, version, expected.Value), cancellationToken))
			: ProblemResults.From(expected.Failure);
	}

	private static readonly JsonSerializerOptions DefinitionOptions = new(JsonSerializerDefaults.Web);

	internal static RegistryWorkflowDefinitionDto ToDto(StoredWorkflowDefinition d) => new(
		d.WorkflowId,
		d.WorkflowVersion,
		d.DefinitionDigest,
		JsonSerializer.Deserialize<WorkflowDefinitionContentDto>(d.DefinitionJson, DefinitionOptions)!,
		d.CreatedAt,
		d.CreatedBy);

	private static DefaultWorkflowDto ToDto(StoredDefaultWorkflow d) => new(d.WorkflowId, d.WorkflowVersion, d.ChangedAt, d.ChangedBy);

	private static async Task<T?> ReadBodyAsync<T>(HttpContext http, CancellationToken cancellationToken)
		where T : class
	{
		try
		{
			return await http.Request.ReadFromJsonAsync<T>(cancellationToken);
		}
		catch (JsonException)
		{
			return null;
		}
	}

	private static async Task<IResult> DefineWorkflowAsync(HttpContext http, DefineWorkflowHandler handler, CancellationToken cancellationToken)
	{
		var body = await ReadBodyAsync<DefineWorkflowRequest>(http, cancellationToken);
		var command = new DefineWorkflowCommand(
			body?.WorkflowId,
			body?.WorkflowVersion,
			body?.RequestedCapabilities,
			body?.CompletionPolicy,
			body?.SegmentationPolicy is { } s ? new WorkflowSegmentationCommand(s.Version, s.DurationSeconds, s.Digest) : null,
			body?.Nodes?.Select(n => new WorkflowNodeCommand(n.NodeKey, n.ProfileId, n.ProfileVersion)).ToArray(),
			body?.Edges?.Select(e => new WorkflowEdgeCommand(
				e.UpstreamNodeKey,
				e.DependentNodeKey,
				e.Kind,
				e.Predicate is { } p ? new ConditionalPredicate(p.Kind ?? "", p.CapabilityId, p.NodeKey) : null)).ToArray());
		var result = await handler.HandleAsync(command, cancellationToken);
		if (result.Failure is { } failure)
		{
			return ProblemResults.From(failure);
		}

		if (result.Definition is null)
		{
			return ProblemResults.Problem(StatusCodes.Status422UnprocessableEntity, DefineWorkflowHandler.WorkflowInvalid, result.Violations);
		}

		var definition = result.Definition;
		http.Response.Headers.Location =
			$"{WorkflowsPath}/{Uri.EscapeDataString(definition.WorkflowId)}/versions/{Uri.EscapeDataString(definition.WorkflowVersion)}";
		return Results.Json(ToDto(definition), statusCode: StatusCodes.Status201Created);
	}

	private static async Task<IResult> ListWorkflowsAsync(ListWorkflowsHandler handler, CancellationToken cancellationToken)
	{
		var result = await handler.HandleAsync(cancellationToken);
		return result.IsSuccess
			? Results.Json(new WorkflowListDto(result.Value.Select(d => new WorkflowListItemDto(
				d.WorkflowId,
				d.WorkflowVersion,
				d.DefinitionDigest,
				d.CreatedAt)).ToArray()))
			: ProblemResults.From(result.Failure);
	}

	private static async Task<IResult> GetWorkflowAsync(
		string workflowId, string version, GetWorkflowHandler handler, CancellationToken cancellationToken)
	{
		var result = await handler.HandleAsync(workflowId, version, cancellationToken);
		return result.IsSuccess ? Results.Json(ToDto(result.Value)) : ProblemResults.From(result.Failure);
	}

	private static IResult DefaultResult(HttpContext http, OperationResult<StoredDefaultWorkflow> result)
	{
		if (!result.IsSuccess)
		{
			return ProblemResults.From(result.Failure);
		}

		http.Response.Headers.ETag = IfMatchHeader.Format(result.Value.Version);
		return Results.Json(ToDto(result.Value));
	}

	private static async Task<IResult> GetDefaultWorkflowAsync(
		HttpContext http, GetDefaultWorkflowHandler handler, CancellationToken cancellationToken) =>
		DefaultResult(http, await handler.HandleAsync(cancellationToken));

	private static async Task<IResult> SetDefaultWorkflowAsync(
		HttpContext http, SetDefaultWorkflowHandler handler, CancellationToken cancellationToken)
	{
		long? expected = null;
		if (http.Request.Headers.ContainsKey("If-Match"))
		{
			var parsed = IfMatchHeader.Parse(http.Request);
			if (!parsed.IsSuccess)
			{
				return ProblemResults.From(parsed.Failure);
			}

			expected = parsed.Value;
		}

		var body = await ReadBodyAsync<SetDefaultWorkflowRequest>(http, cancellationToken);
		var result = await handler.HandleAsync(new SetDefaultWorkflowCommand(body?.WorkflowId, body?.WorkflowVersion, expected), cancellationToken);
		if (result.Failure is { } failure)
		{
			return ProblemResults.From(failure);
		}

		if (result.Default is null)
		{
			return ProblemResults.Problem(StatusCodes.Status422UnprocessableEntity, SetDefaultWorkflowHandler.WorkflowNotResolvable);
		}

		http.Response.Headers.ETag = IfMatchHeader.Format(result.Default.Version);
		return Results.Json(ToDto(result.Default));
	}

	private static async Task<IResult> RetireProfileAsync(
		string profileId, int version, HttpContext http, RetireAnalystProfileHandler handler, CancellationToken cancellationToken)
	{
		var expected = IfMatchHeader.Parse(http.Request);
		return expected.IsSuccess
			? ProfileResult(http, await handler.HandleAsync(new ProfileTransitionCommand(profileId, version, expected.Value), cancellationToken))
			: ProblemResults.From(expected.Failure);
	}
}
