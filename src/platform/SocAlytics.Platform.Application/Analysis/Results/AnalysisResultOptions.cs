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

    public long MaxFactArtifactBytes { get; set; }

    public int MaxFactPayloadBytes { get; set; }

    public int MaxFactsPerResult { get; set; }

    public PayloadValidationOptions PayloadValidation { get; set; } = new();
}

public sealed class PayloadValidationOptions
{
    public TimeSpan MaxDuration { get; set; } = TimeSpan.FromSeconds(5);
}

internal sealed class AnalysisResultOptionsValidator : IValidateOptions<AnalysisResultOptions>
{
    private const long MinManifestBytes = 1024;
    private const long MinFactArtifactBytes = 1024;
    private const int MinFactPayloadBytes = 256;
    private const int MaxAllowedFactPayloadBytes = 1_048_576;
    private const int MaxAllowedFactsPerResult = 1_000_000;
    private static readonly TimeSpan MinPayloadValidationDuration = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxPayloadValidationDuration = TimeSpan.FromSeconds(60);

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

        if (options.MaxFactArtifactBytes < MinFactArtifactBytes || options.MaxFactArtifactBytes > options.MaxObjectBytes)
        {
            failures.Add($"{section}:MaxFactArtifactBytes must be between {MinFactArtifactBytes} and MaxObjectBytes.");
        }

        if (options.MaxFactPayloadBytes < MinFactPayloadBytes || options.MaxFactPayloadBytes > MaxAllowedFactPayloadBytes)
        {
            failures.Add($"{section}:MaxFactPayloadBytes must be between {MinFactPayloadBytes} and {MaxAllowedFactPayloadBytes}.");
        }

        if (options.MaxFactsPerResult < 1 || options.MaxFactsPerResult > MaxAllowedFactsPerResult)
        {
            failures.Add($"{section}:MaxFactsPerResult must be between 1 and {MaxAllowedFactsPerResult}.");
        }

        var payloadDuration = options.PayloadValidation?.MaxDuration;
        if (payloadDuration is null || payloadDuration < MinPayloadValidationDuration || payloadDuration > MaxPayloadValidationDuration)
        {
            failures.Add($"{section}:PayloadValidation:MaxDuration must be between 1 and 60 seconds.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
