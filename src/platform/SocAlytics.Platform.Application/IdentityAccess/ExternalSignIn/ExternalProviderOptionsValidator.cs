using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace SocAlytics.Platform.Application.IdentityAccess.ExternalSignIn;

public sealed partial class ExternalProviderOptionsValidator(bool allowInsecureUrls) : IValidateOptions<IdentityAccessOptions>
{
	private const string Section = IdentityAccessOptions.SectionName + ":ExternalProviders";

	[GeneratedRegex("^[a-z0-9-]{1,32}$")]
	private static partial Regex ProviderIdPattern();

	public ValidateOptionsResult Validate(string? name, IdentityAccessOptions options)
	{
		var failures = new List<string>();
		if (options.ExternalProviders.Count > 0)
		{
			if (options.ExternalSignIn.PendingLifetime < TimeSpan.FromSeconds(1) || options.ExternalSignIn.PendingLifetime > TimeSpan.FromHours(1))
			{
				failures.Add($"{IdentityAccessOptions.SectionName}:ExternalSignIn:PendingLifetime must be between 1 second and 1 hour.");
			}

			if (!IsAcceptableUrl(options.ExternalSignIn.WebUiBaseUrl, allowInsecureUrls, requireBare: true))
			{
				failures.Add($"{IdentityAccessOptions.SectionName}:ExternalSignIn:WebUiBaseUrl must be an absolute URL (HTTPS outside Development and Test).");
			}
		}

		foreach (var (id, provider) in options.ExternalProviders)
		{
			var prefix = $"{Section}:{id}";
			if (!ProviderIdPattern().IsMatch(id))
			{
				failures.Add($"{prefix}: the provider identifier must match [a-z0-9-]{{1,32}}.");
			}

			if (string.IsNullOrWhiteSpace(provider.DisplayName))
			{
				failures.Add($"{prefix}:DisplayName is required.");
			}

			if (!IsAcceptableUrl(provider.Authority, allowInsecureUrls, requireBare: false))
			{
				failures.Add($"{prefix}:Authority must be an absolute URL (HTTPS outside Development and Test).");
			}

			if (string.IsNullOrWhiteSpace(provider.ClientId))
			{
				failures.Add($"{prefix}:ClientId is required.");
			}

			if (string.IsNullOrWhiteSpace(provider.ClientSecret))
			{
				failures.Add($"{prefix}:ClientSecret is required.");
			}

			if (string.IsNullOrWhiteSpace(provider.Scopes))
			{
				failures.Add($"{prefix}:Scopes must not be empty.");
			}

			if (provider.TokenEndpointAuthMethod is not (IdentityAccessOptions.ExternalProviderOptions.BasicAuthMethod
				or IdentityAccessOptions.ExternalProviderOptions.PostAuthMethod))
			{
				failures.Add($"{prefix}:TokenEndpointAuthMethod must be client_secret_basic or client_secret_post.");
			}
		}

		return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
	}

	private static bool IsAcceptableUrl(string value, bool allowInsecure, bool requireBare)
	{
		if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Query.Length > 0 || uri.Fragment.Length > 0)
		{
			return false;
		}

		if (requireBare && uri.AbsolutePath != "/")
		{
			return false;
		}

		return uri.Scheme == Uri.UriSchemeHttps || (allowInsecure && uri.Scheme == Uri.UriSchemeHttp);
	}
}
