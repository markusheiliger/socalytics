using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace SocAlytics.Platform.Infrastructure.Registry.Oci;

internal sealed class ImageRegistryOptions
{
    public const string SectionName = "Registry:Images";

    public string RegistryHost { get; set; } = string.Empty;

    public string RepositoryPrefix { get; set; } = string.Empty;

    public string? Username { get; set; }

    public string? Token { get; set; }

    /// <summary>Talks plain HTTP to the registry; accepted only in Development and Test.</summary>
    public bool UseHttp { get; set; }

    /// <summary>Upper bound for the lifetime of a pull credential handed to an Analyst Manager.</summary>
    public TimeSpan PullCredentialLifetime { get; set; } = TimeSpan.FromMinutes(5);
}

internal sealed class ImageRegistryOptionsValidator(IHostEnvironment environment) : IValidateOptions<ImageRegistryOptions>
{
    public ValidateOptionsResult Validate(string? name, ImageRegistryOptions options)
    {
        var failures = new List<string>();
        if (options.UseHttp && !(environment.IsDevelopment() || environment.IsEnvironment("Test")))
        {
            failures.Add("Registry:Images:UseHttp is allowed only in the Development and Test environments; HTTPS is required.");
        }

        if (string.IsNullOrWhiteSpace(options.Username) != string.IsNullOrWhiteSpace(options.Token))
        {
            failures.Add("Registry:Images:Username and Registry:Images:Token must be configured together.");
        }

        if (options.PullCredentialLifetime <= TimeSpan.Zero)
        {
            failures.Add("Registry:Images:PullCredentialLifetime must be positive.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
