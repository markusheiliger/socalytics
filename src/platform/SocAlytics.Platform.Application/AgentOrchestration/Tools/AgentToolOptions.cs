using Microsoft.Extensions.Options;

namespace SocAlytics.Platform.Application.AgentOrchestration.Tools;

// No production defaults: development and test values come from configuration (research R10).
public sealed class AgentToolOptions
{
    public const string SectionName = AgentOrchestrationOptions.SectionName + ":Tools";

    public int? MaxCallsPerAttempt { get; set; }

    public int? MaxClipResolutionsPerAttempt { get; set; }
}

internal sealed class AgentToolOptionsValidator : IValidateOptions<AgentToolOptions>
{
    public ValidateOptionsResult Validate(string? name, AgentToolOptions options)
    {
        var failures = new List<string>();
        Require(failures, nameof(options.MaxCallsPerAttempt), options.MaxCallsPerAttempt);
        Require(failures, nameof(options.MaxClipResolutionsPerAttempt), options.MaxClipResolutionsPerAttempt);
        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private static void Require(List<string> failures, string property, int? value)
    {
        if (value is null)
        {
            failures.Add($"{AgentToolOptions.SectionName}:{property} is required.");
        }
        else if (value < 1)
        {
            failures.Add($"{AgentToolOptions.SectionName}:{property} must be positive.");
        }
    }
}
