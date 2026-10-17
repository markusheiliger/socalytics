using Microsoft.Extensions.Options;
using SocAlytics.Platform.Api.Http;
using SocAlytics.Platform.Api.Security;
using SocAlytics.Platform.Application.IdentityAccess;
using SocAlytics.Platform.Application.IdentityAccess.ExternalSignIn;
using SocAlytics.Platform.Application.Registry;

namespace SocAlytics.Platform.Api.Endpoints.IdentityAccess;

internal sealed record IdentityProviderDto(string Id, string DisplayName);

internal sealed record IdentityProvidersDto(IReadOnlyList<IdentityProviderDto> Providers);

internal sealed record StartExternalSignInRequest(string? Provider);

internal sealed record ExternalAuthorizationDto(string AuthorizationUrl);

internal static class ExternalSignInEndpoints
{
	private const string CallbackPath = "/api/v1/session/external/callback";

	public static IEndpointRouteBuilder MapExternalSignInEndpoints(this IEndpointRouteBuilder routes)
	{
		routes.MapGet("/api/v1/identity-providers", ListProviders)
			.AllowAnonymous()
			.WithName("listIdentityProviders")
			.WithTags("Session")
			.Produces<IdentityProvidersDto>();

		routes.MapPost("/api/v1/session/external", StartAsync)
			.AllowAnonymous()
			.AddEndpointFilter<JsonOnlyFilter>()
			.WithName("startExternalSignIn")
			.WithTags("Session")
			.Produces<ExternalAuthorizationDto>()
			.ProducesProblem(StatusCodes.Status400BadRequest)
			.ProducesProblem(StatusCodes.Status415UnsupportedMediaType)
			.ProducesProblem(StatusCodes.Status503ServiceUnavailable);

		routes.MapGet(CallbackPath, CompleteAsync)
			.AllowAnonymous()
			.WithName("completeExternalSignIn")
			.WithTags("Session")
			.Produces(StatusCodes.Status302Found);
		return routes;
	}

	private static IResult ListProviders(HttpContext http, IOptions<IdentityAccessOptions> options)
	{
		var providers = options.Value.ExternalProviders
			.OrderBy(p => p.Key, StringComparer.Ordinal)
			.Select(p => new IdentityProviderDto(p.Key, p.Value.DisplayName))
			.ToArray();
		http.Response.Headers.CacheControl = "no-store";
		return Results.Ok(new IdentityProvidersDto(providers));
	}

	private static async Task<IResult> StartAsync(
		HttpContext http,
		StartExternalSignInHandler handler,
		IOptions<StampOptions> stamp,
		CancellationToken cancellationToken)
	{
		StartExternalSignInRequest? request;
		try
		{
			request = await http.Request.ReadFromJsonAsync<StartExternalSignInRequest>(cancellationToken);
		}
		catch (System.Text.Json.JsonException)
		{
			request = null;
		}

		if (string.IsNullOrWhiteSpace(request?.Provider))
		{
			return ProblemResults.Problem(StatusCodes.Status400BadRequest, "invalid-request");
		}

		var result = await handler.HandleAsync(
			new StartExternalSignInCommand(request.Provider, stamp.Value.Build(CallbackPath).ToString()),
			cancellationToken);
		switch (result.Outcome)
		{
			case StartExternalSignInOutcome.UnknownProvider:
				return ProblemResults.Problem(StatusCodes.Status400BadRequest, "invalid-request");
			case StartExternalSignInOutcome.ProviderUnavailable:
				return ProblemResults.Problem(StatusCodes.Status503ServiceUnavailable, "provider-unavailable");
		}

		ExternalBindingCookie.Write(http.Response, result.BrowserBinding!, result.BindingLifetime);
		http.Response.Headers.CacheControl = "no-store";
		return Results.Ok(new ExternalAuthorizationDto(result.AuthorizationUrl!.ToString()));
	}

	private static async Task<IResult> CompleteAsync(
		HttpContext http,
		CompleteExternalSignInHandler handler,
		IOptions<IdentityAccessOptions> options,
		IOptions<StampOptions> stamp,
		CancellationToken cancellationToken)
	{
		var webUi = options.Value.ExternalSignIn.WebUiBaseUrl.TrimEnd('/');
		var query = http.Request.Query;
		var presented = SessionCookie.Read(http.Request);
		var command = new CompleteExternalSignInCommand(
			query["state"].ToString() is { Length: > 0 } state ? state : null,
			query["code"].ToString() is { Length: > 0 } code ? code : null,
			query.ContainsKey("error"),
			ExternalBindingCookie.Read(http.Request),
			stamp.Value.Build(CallbackPath).ToString(),
			presented is null ? null : SessionCookie.Hash(presented));

		var result = await handler.HandleAsync(command, cancellationToken);
		ExternalBindingCookie.Clear(http.Response);
		http.Response.Headers.CacheControl = "no-store";
		if (!result.IsSuccess)
		{
			return Results.Redirect($"{webUi}/sign-in?external=failed");
		}

		SessionCookie.Write(http.Response, result.Value.RawToken);
		return Results.Redirect($"{webUi}/");
	}
}
