using Microsoft.Extensions.Options;

namespace SocAlytics.Platform.Application.Analysis.Evidence;

public sealed class EvidenceOptions
{
    public const string SectionName = "Analysis:Evidence";

    public TimeSpan? GrantLifetime { get; set; }

    public TimeSpan? RetryAfter { get; set; }

    public int? ResolutionsPerMinute { get; set; }

    public TimeSpan? MaxDuration { get; set; }

    public TimeSpan? MaxContext { get; set; }
}

internal sealed class EvidenceOptionsValidator : IValidateOptions<EvidenceOptions>
{
    public ValidateOptionsResult Validate(string? name, EvidenceOptions options)
    {
        var failures = new List<string>();

        CheckRange(failures, nameof(options.GrantLifetime), options.GrantLifetime,
            TimeSpan.FromSeconds(30), TimeSpan.FromHours(1));
        CheckRange(failures, nameof(options.RetryAfter), options.RetryAfter,
            TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(5));
        CheckRange(failures, nameof(options.MaxDuration), options.MaxDuration,
            TimeSpan.FromSeconds(1), TimeSpan.FromHours(1));
        CheckRange(failures, nameof(options.MaxContext), options.MaxContext,
            TimeSpan.Zero, TimeSpan.FromMinutes(1));

        if (options.ResolutionsPerMinute is null)
        {
            failures.Add($"{EvidenceOptions.SectionName}:{nameof(options.ResolutionsPerMinute)} is required.");
        }
        else if (options.ResolutionsPerMinute < 1 || options.ResolutionsPerMinute > 10000)
        {
            failures.Add($"{EvidenceOptions.SectionName}:{nameof(options.ResolutionsPerMinute)} must be between 1 and 10000.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private static void CheckRange(List<string> failures, string field, TimeSpan? value, TimeSpan min, TimeSpan max)
    {
        if (value is null)
        {
            failures.Add($"{EvidenceOptions.SectionName}:{field} is required.");
        }
        else if (value < min || value > max)
        {
            failures.Add($"{EvidenceOptions.SectionName}:{field} must be between {min:c} and {max:c}.");
        }
    }
}
