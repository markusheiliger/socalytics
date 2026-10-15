using Microsoft.Extensions.Options;

namespace SocAlytics.Platform.Application.Analysis.Results;

public sealed class AnalysisReadOptions
{
    public const string SectionName = "Analysis:Reads";

    public TimeSpan GrantLifetime { get; set; }

    public int MaxPageSize { get; set; }
}

internal sealed class AnalysisReadOptionsValidator : IValidateOptions<AnalysisReadOptions>
{
    private const int MaxAllowedPageSize = 1000;

    public ValidateOptionsResult Validate(string? name, AnalysisReadOptions options)
    {
        var failures = new List<string>();
        var section = AnalysisReadOptions.SectionName;

        if (options.GrantLifetime <= TimeSpan.Zero || options.GrantLifetime > TimeSpan.FromHours(1))
        {
            failures.Add($"{section}:GrantLifetime must be positive and at most 1 hour.");
        }

        if (options.MaxPageSize < 1 || options.MaxPageSize > MaxAllowedPageSize)
        {
            failures.Add($"{section}:MaxPageSize must be between 1 and {MaxAllowedPageSize}.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
