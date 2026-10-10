using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace SocAlytics.Platform.Api.Http;

internal sealed class OpenApiSecuritySchemesTransformer : IOpenApiDocumentTransformer
{
	internal const string SessionCookieScheme = "sessionCookie";
	internal const string AntiforgeryHeaderScheme = "antiforgeryHeader";

	private static readonly HashSet<string> UnauthenticatedOperations = ["signIn", "redeemCredential"];

	public Task TransformAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken cancellationToken)
	{
		document.Components ??= new OpenApiComponents();
		document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
		document.Components.SecuritySchemes[SessionCookieScheme] = new OpenApiSecurityScheme
		{
			Type = SecuritySchemeType.ApiKey,
			In = ParameterLocation.Cookie,
			Name = "__Host-socalytics-session",
			Description = "Opaque server-validated session token (Secure, HttpOnly, SameSite=Strict).",
		};
		document.Components.SecuritySchemes[AntiforgeryHeaderScheme] = new OpenApiSecurityScheme
		{
			Type = SecuritySchemeType.ApiKey,
			In = ParameterLocation.Header,
			Name = "X-CSRF-Token",
			Description = "Anti-forgery token derived from the current session token, returned by signIn and getSession.",
		};

		document.Security = [Requirement(document, SessionCookieScheme)];

		foreach (var (_, item) in document.Paths)
		{
			if (item.Operations is null)
			{
				continue;
			}

			foreach (var (method, operation) in item.Operations)
			{
				if (operation.OperationId is not null && UnauthenticatedOperations.Contains(operation.OperationId))
				{
					operation.Security = [];
				}
				else if (method != HttpMethod.Get && method != HttpMethod.Head && method != HttpMethod.Options)
				{
					operation.Security = [Requirement(document, SessionCookieScheme, AntiforgeryHeaderScheme)];
				}
			}
		}

		return Task.CompletedTask;
	}

	private static OpenApiSecurityRequirement Requirement(OpenApiDocument document, params string[] schemes)
	{
		var requirement = new OpenApiSecurityRequirement();
		foreach (var scheme in schemes)
		{
			requirement[new OpenApiSecuritySchemeReference(scheme, document)] = [];
		}

		return requirement;
	}
}
