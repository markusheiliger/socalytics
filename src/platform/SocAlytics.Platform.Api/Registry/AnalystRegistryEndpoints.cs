using System.Text.Json;
using System.Text.Json.Nodes;
using SocAlytics.Platform.Api.Http;
using SocAlytics.Platform.Api.Security;
using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.Registry;
using SocAlytics.Platform.Application.Registry.Images;
using SocAlytics.Platform.Application.Registry.Profiles;
using SocAlytics.Platform.Domain.Analysis;
using SocAlytics.Platform.Domain.Registry;

namespace SocAlytics.Platform.Api.Registry;

internal sealed record RegisterAnalystImageBody(string? Reference);

internal sealed record AnalystProfileContentBody(
	string? CapabilityId,
	string? CapabilityVersion,
	string? ModelDigest,
	string[]? RuntimeConstraints,
	string[]? Accelerators,
	Dictionary<string, string>? Resources,
	int MaxAttempts,
	int LeaseSeconds,
	int StaleTimeoutSeconds,
	int ExecutionTimeoutSeconds,
	string? VideoPolicy,
	Dictionary<string, string>? InputSchemaVersions,
	string? OutputSchemaVersion);

internal sealed record CreateAnalystProfileBody(string? ProfileId, string? ImageDigest, AnalystProfileContentBody? Content);

internal static class AnalystRegistryEndpoints
{
	private const string ImagesPath = MemberApiRouteGroup.Prefix + "/registry/analyst-images";
	private const string ProfilesPath = MemberApiRouteGroup.Prefix + "/registry/analyst-profiles";

	public static IEndpointRouteBuilder MapAnalystRegistryEndpoints(this IEndpointRouteBuilder routes)
	{
		var group = routes.MapMemberApi().WithTags("Analyst registry");
		group.MapPost("/registry/analyst-images", RegisterImageAsync)
			.AddEndpointFilter<JsonOnlyFilter>()
			.WithName("registerAnalystImage");
		group.MapGet("/registry/analyst-images", ListImagesAsync)
			.WithName("listAnalystImages");
		group.MapGet("/registry/analyst-images/{digest}", GetImageAsync)
			.WithName("getAnalystImage");
		group.MapPost("/registry/analyst-profiles", CreateProfileAsync)
			.AddEndpointFilter<JsonOnlyFilter>()
			.WithName("createAnalystProfile");
		group.MapGet("/registry/analyst-profiles", ListProfilesAsync)
			.WithName("listAnalystProfiles");
		group.MapGet("/registry/analyst-profiles/{profileId}/versions/{version:int}", GetProfileAsync)
			.WithName("getAnalystProfile");
		group.MapPost("/registry/analyst-profiles/{profileId}/versions/{version:int}/approval", ApproveProfileAsync)
			.WithName("approveAnalystProfile");
		group.MapPost("/registry/analyst-profiles/{profileId}/versions/{version:int}/retirement", RetireProfileAsync)
			.WithName("retireAnalystProfile");
		return routes;
	}

	internal static object ToDto(StoredCapabilityDeclaration s)
	{
		var d = s.Declaration;
		return new
		{
			imageDigest = d.ImageDigest,
			repository = s.Repository,
			manifestDigest = s.ManifestDigest,
			capability = new
			{
				id = d.CapabilityId,
				version = d.CapabilityVersion,
				tier = AnalysisPersistedNames.ToPersistedName(d.Tier),
				executionScope = AnalysisPersistedNames.ToPersistedName(d.ExecutionScope),
			},
			modelDigest = d.ModelDigest,
			manifest = JsonNode.Parse(s.ManifestJson),
			source = s.Source,
			registeredAt = s.RegisteredAt,
			registeredBy = s.RegisteredBy,
		};
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
		RegisterAnalystImageBody? body;
		try
		{
			body = await http.Request.ReadFromJsonAsync<RegisterAnalystImageBody>(cancellationToken);
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
			? Results.Json(new
			{
				items = result.Value.Select(s => new
				{
					imageDigest = s.Declaration.ImageDigest,
					capabilityId = s.Declaration.CapabilityId,
					capabilityVersion = s.Declaration.CapabilityVersion,
					registeredAt = s.RegisteredAt,
				}).ToArray(),
			})
			: ProblemResults.From(result.Failure);
	}

	private static async Task<IResult> GetImageAsync(string digest, GetAnalystImageHandler handler, CancellationToken cancellationToken)
	{
		var result = await handler.HandleAsync(digest, cancellationToken);
		return result.IsSuccess ? Results.Json(ToDto(result.Value)) : ProblemResults.From(result.Failure);
	}

	internal static object ToDto(StoredAnalystProfile s)
	{
		var p = s.Profile;
		var c = p.Content;
		return new
		{
			profileId = p.ProfileId,
			version = p.Version,
			profileDigest = p.Digest.ToString(),
			imageDigest = c.ImageDigest,
			state = p.State.ToPersistedName(),
			content = new
			{
				capabilityId = c.CapabilityId,
				capabilityVersion = c.CapabilityVersion,
				modelDigest = c.ModelDigest,
				runtimeConstraints = c.RuntimeConstraints,
				accelerators = c.Accelerators,
				resources = c.Resources,
				maxAttempts = c.MaxAttempts,
				leaseSeconds = c.LeaseSeconds,
				staleTimeoutSeconds = c.StaleTimeoutSeconds,
				executionTimeoutSeconds = c.ExecutionTimeoutSeconds,
				videoPolicy = c.VideoPolicy,
				inputSchemaVersions = c.InputSchemaVersions,
				outputSchemaVersion = c.OutputSchemaVersion,
			},
			createdAt = p.CreatedAt,
			createdBy = p.CreatedBy,
			approvedAt = p.ApprovedAt,
			approvedBy = p.ApprovedBy,
			retiredAt = p.RetiredAt,
			retiredBy = p.RetiredBy,
		};
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
		CreateAnalystProfileBody? body;
		try
		{
			body = await http.Request.ReadFromJsonAsync<CreateAnalystProfileBody>(cancellationToken);
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
			? Results.Json(new { items = result.Value.Select(ToDto).ToArray() })
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

	private static async Task<IResult> RetireProfileAsync(
		string profileId, int version, HttpContext http, RetireAnalystProfileHandler handler, CancellationToken cancellationToken)
	{
		var expected = IfMatchHeader.Parse(http.Request);
		return expected.IsSuccess
			? ProfileResult(http, await handler.HandleAsync(new ProfileTransitionCommand(profileId, version, expected.Value), cancellationToken))
			: ProblemResults.From(expected.Failure);
	}
}
