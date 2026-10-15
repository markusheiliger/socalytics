using Microsoft.AspNetCore.Authentication;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Options;
using SocAlytics.Platform.Api.Security;
using SocAlytics.Platform.Application.Registry;

namespace SocAlytics.Platform.Api.Registry;

internal static class RegistryApiRegistration
{
	public static WebApplicationBuilder AddRegistryApi(this WebApplicationBuilder builder)
	{
		builder.Services.AddSingleton<IValidateOptions<StampOptions>>(
			new StampOptionsValidator(builder.Environment.IsDevelopment()));
		builder.Services.AddOptions<StampOptions>().ValidateOnStart();
		builder.Services.AddSingleton<IValidateOptions<RegistryOptions>>(
			new RegistryOptionsValidator(builder.Environment.IsDevelopment(), builder.Environment.IsDevelopment() || builder.Environment.IsEnvironment("Test")));
		builder.Services.AddOptions<RegistryOptions>().ValidateOnStart();
		builder.Services.ConfigureHttpJsonOptions(options =>
			options.SerializerOptions.TypeInfoResolver = (options.SerializerOptions.TypeInfoResolver ?? new DefaultJsonTypeInfoResolver())
				.WithAddedModifier(RegistrationDtoJson.OmitOperationsUnlessIncluded));
		builder.Services.AddHostedService<RegistrationExpiryWorker>();
		builder.Services.AddRegistryRateLimiting();
		builder.Services.AddAuthentication()
			.AddScheme<AuthenticationSchemeOptions, AnalystManagerDPoPAuthenticationHandler>(
				AnalystManagerDPoPDefaults.AuthenticationScheme, null);
		builder.Services.AddAuthorizationBuilder()
			.AddPolicy(AuthorizationPolicyNames.AnalystManager, policy => policy
				.AddAuthenticationSchemes(AnalystManagerDPoPDefaults.AuthenticationScheme)
				.RequireAuthenticatedUser()
				.RequireClaim(AnalystManagerClaimTypes.Scope, AnalystManagerDPoPDefaults.ScopeValue));
		return builder;
	}

	public static WebApplication MapRegistryEndpoints(this WebApplication app)
	{
		app.UseRateLimiter();
		app.MapAnalystManagerPairingEndpoints();
		app.MapAnalystManagerRegistrationEndpoints();
		app.MapAnalystManagerTokenEndpoint();
		app.MapAnalystManagerSelfEndpoints();
		app.MapAnalystRegistryEndpoints();
		return app;
	}
}

public sealed class StampOptionsValidator(bool allowHttp) : IValidateOptions<StampOptions>
{
	public ValidateOptionsResult Validate(string? name, StampOptions options)
	{
		var failures = new List<string>();
		if (string.IsNullOrWhiteSpace(options.Id))
		{
			failures.Add("Stamp:Id is required.");
		}

		if (!Uri.TryCreate(options.PublicBaseUri, UriKind.Absolute, out var uri)
			|| !(uri.Scheme == Uri.UriSchemeHttps || (allowHttp && uri.Scheme == Uri.UriSchemeHttp))
			|| (uri.AbsolutePath != "/" && uri.AbsolutePath.Length > 0))
		{
			failures.Add(allowHttp
				? "Stamp:PublicBaseUri must be an absolute http or https origin."
				: "Stamp:PublicBaseUri must be an absolute https origin.");
		}

		return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
	}
}

public sealed class RegistryOptionsValidator(bool allowHttp, bool allowAllSoftwareKeys = false) : IValidateOptions<RegistryOptions>
{
	public ValidateOptionsResult Validate(string? name, RegistryOptions options)
	{
		if (options.SoftwareKeys == SoftwareKeyPolicy.All && !allowAllSoftwareKeys)
		{
			return ValidateOptionsResult.Fail("Registry:SoftwareKeys 'all' is allowed only in the Development and Test environments.");
		}

		var value = options.Pairing.VerificationBaseUri;
		if (value is null)
		{
			return ValidateOptionsResult.Success;
		}

		if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
			|| !(uri.Scheme == Uri.UriSchemeHttps || (allowHttp && uri.Scheme == Uri.UriSchemeHttp))
			|| uri.AbsolutePath != "/"
			|| !string.IsNullOrEmpty(uri.Query)
			|| !string.IsNullOrEmpty(uri.Fragment)
			|| !string.IsNullOrEmpty(uri.UserInfo)
			|| value.EndsWith('?') || value.EndsWith('#'))
		{
			return ValidateOptionsResult.Fail(allowHttp
				? "Registry:Pairing:VerificationBaseUri must be an absolute http or https origin without path, query, or fragment."
				: "Registry:Pairing:VerificationBaseUri must be an absolute https origin without path, query, or fragment.");
		}

		return ValidateOptionsResult.Success;
	}
}
