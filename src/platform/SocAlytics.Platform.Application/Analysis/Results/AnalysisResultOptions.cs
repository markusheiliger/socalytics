using Microsoft.Extensions.Options;

namespace SocAlytics.Platform.Application.Analysis.Results;

public sealed class AnalysisResultOptions
{
    public const string SectionName = "Analysis:Results";

    public TimeSpan GrantLifetime { get; set; }

    public int MaxObjectsPerAttempt { get; set; }

    public long MaxObjectBytes { get; set; }

    public long MaxAttemptBytes { get; set; }

    public long MaxManifestBytes { get; set; }
}

internal sealed class AnalysisResultOptionsValidator : IValidateOptions<AnalysisResultOptions>
{
    private const long MinManifestBytes = 1024;

    public ValidateOptionsResult Validate(string? name, AnalysisResultOptions options)
    {
        var failures = new List<string>();
        var section = AnalysisResultOptions.SectionName;

        if (options.GrantLifetime <= TimeSpan.Zero || options.GrantLifetime > TimeSpan.FromHours(1))
        {
            failures.Add($"{section}:GrantLifetime must be positive and at most 1 hour.");
        }

        if (options.MaxObjectsPerAttempt < 1)
        {
            failures.Add($"{section}:MaxObjectsPerAttempt must be at least 1.");
        }

        if (options.MaxManifestBytes < MinManifestBytes)
        {
            failures.Add($"{section}:MaxManifestBytes must be at least {MinManifestBytes}.");
        }

        if (options.MaxObjectBytes < options.MaxManifestBytes)
        {
            failures.Add($"{section}:MaxObjectBytes must not be less than MaxManifestBytes.");
        }

        if (options.MaxAttemptBytes < options.MaxObjectBytes)
        {
            failures.Add($"{section}:MaxAttemptBytes must not be less than MaxObjectBytes.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
