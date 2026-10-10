using Microsoft.Extensions.Options;
using SocAlytics.Platform.Application.Registry;

namespace SocAlytics.Platform.Api.Registry;

internal static class RegistryApiRegistration
{
	public static WebApplicationBuilder AddRegistryApi(this WebApplicationBuilder builder)
	{
		builder.Services.AddSingleton<IValidateOptions<StampOptions>>(
			new StampOptionsValidator(builder.Environment.IsDevelopment()));
		builder.Services.AddOptions<StampOptions>().ValidateOnStart();
		builder.Services.AddHostedService<RegistrationExpiryWorker>();
		builder.Services.AddRegistryRateLimiting();
		return builder;
	}

	public static WebApplication MapRegistryEndpoints(this WebApplication app)
	{
		app.UseRateLimiter();
		app.MapAnalystManagerPairingEndpoints();
		app.MapAnalystManagerRegistrationEndpoints();
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
