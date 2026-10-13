using System.Text.Json;
using System.Text.Json.Nodes;
using SocAlytics.Platform.Api.Http;
using SocAlytics.Platform.Api.Security;
using SocAlytics.Platform.Application.Registry;
using SocAlytics.Platform.Application.Registry.Images;
using SocAlytics.Platform.Domain.Analysis;

namespace SocAlytics.Platform.Api.Registry;

internal sealed record RegisterAnalystImageBody(string? Reference);

internal static class AnalystRegistryEndpoints
{
	private const string ImagesPath = MemberApiRouteGroup.Prefix + "/registry/analyst-images";

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
}
