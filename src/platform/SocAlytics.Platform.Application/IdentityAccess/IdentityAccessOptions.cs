using System.ComponentModel.DataAnnotations;

namespace SocAlytics.Platform.Application.IdentityAccess;

public sealed class IdentityAccessOptions : IValidatableObject
{
	public const string SectionName = "IdentityAccess";

	public SessionOptions Session { get; set; } = new();

	public LockoutOptions Lockout { get; set; } = new();

	public PasswordOptions Password { get; set; } = new();

	public OneTimeCredentialOptions OneTimeCredential { get; set; } = new();

	public Dictionary<string, ExternalProviderOptions> ExternalProviders { get; set; } = new(StringComparer.Ordinal);

	public ExternalSignInOptions ExternalSignIn { get; set; } = new();

	public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
	{
		var results = new List<ValidationResult>();
		Validate(Session, nameof(Session), results);
		Validate(Lockout, nameof(Lockout), results);
		Validate(Password, nameof(Password), results);
		Validate(OneTimeCredential, nameof(OneTimeCredential), results);
		return results;
	}

	private static void Validate(object section, string name, List<ValidationResult> results)
	{
		var sectionResults = new List<ValidationResult>();
		Validator.TryValidateObject(section, new ValidationContext(section), sectionResults, validateAllProperties: true);
		foreach (var result in sectionResults)
		{
			var members = result.MemberNames.Select(member => $"{SectionName}:{name}:{member}").ToArray();
			var message = members.Length == 0
				? $"{SectionName}:{name} is invalid."
				: $"{string.Join(", ", members)} is missing or out of range.";
			results.Add(new ValidationResult(message, members));
		}
	}

	public sealed class SessionOptions
	{
		[Range(typeof(TimeSpan), "00:00:01", "3650.00:00:00")]
		public TimeSpan IdleTimeout { get; set; }

		[Range(typeof(TimeSpan), "00:00:01", "3650.00:00:00")]
		public TimeSpan AbsoluteLifetime { get; set; }
	}

	public sealed class LockoutOptions
	{
		[Range(1, 1000)]
		public int MaxFailedAccessAttempts { get; set; }

		[Range(typeof(TimeSpan), "00:00:01", "3650.00:00:00")]
		public TimeSpan LockoutDuration { get; set; }
	}

	public sealed class PasswordOptions
	{
		[Range(1, 1000)]
		public int RequiredLength { get; set; }
	}

	public sealed class OneTimeCredentialOptions
	{
		[Range(typeof(TimeSpan), "00:00:01", "3650.00:00:00")]
		public TimeSpan Lifetime { get; set; }
	}

	public sealed class ExternalProviderOptions
	{
		public const string BasicAuthMethod = "client_secret_basic";
		public const string PostAuthMethod = "client_secret_post";

		public string DisplayName { get; set; } = string.Empty;

		public string Authority { get; set; } = string.Empty;

		public string ClientId { get; set; } = string.Empty;

		public string ClientSecret { get; set; } = string.Empty;

		/// <summary>Space-separated scopes; <c>openid</c> is always requested.</summary>
		public string Scopes { get; set; } = "openid";

		public string TokenEndpointAuthMethod { get; set; } = BasicAuthMethod;
	}

	public sealed class ExternalSignInOptions
	{
		public TimeSpan PendingLifetime { get; set; } = TimeSpan.FromMinutes(5);

		public string WebUiBaseUrl { get; set; } = string.Empty;
	}
}
