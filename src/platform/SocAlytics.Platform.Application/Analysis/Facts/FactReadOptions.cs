using Microsoft.Extensions.Options;

namespace SocAlytics.Platform.Application.Analysis.Facts;

public sealed class FactReadOptions
{
    public const string SectionName = "Analysis:Facts";

    public int DefaultPageSize { get; set; }

    public int MaxPageSize { get; set; }
}

internal sealed class FactReadOptionsValidator : IValidateOptions<FactReadOptions>
{
    private const int MaxAllowedPageSize = 1000;

    public ValidateOptionsResult Validate(string? name, FactReadOptions options)
    {
        var failures = new List<string>();
        var section = FactReadOptions.SectionName;

        if (options.MaxPageSize < 1 || options.MaxPageSize > MaxAllowedPageSize)
        {
            failures.Add($"{section}:MaxPageSize must be between 1 and {MaxAllowedPageSize}.");
        }

        if (options.DefaultPageSize < 1 || options.DefaultPageSize > options.MaxPageSize)
        {
            failures.Add($"{section}:DefaultPageSize must be between 1 and MaxPageSize.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
