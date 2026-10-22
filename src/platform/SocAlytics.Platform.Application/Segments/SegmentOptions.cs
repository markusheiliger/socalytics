using Microsoft.Extensions.Options;

namespace SocAlytics.Platform.Application.Segments;

// Defaults are explicit non-production values; production values are deferred.
public sealed class SegmentMaterializationOptions
{
    public const string SectionName = "Segments:Materialization";

    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(5);

    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromMinutes(5);

    public int MaxConcurrentPerInstance { get; set; } = 2;

    public int MaxAttempts { get; set; } = 3;

    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(30);

    public TimeSpan ExecutionTimeout { get; set; } = TimeSpan.FromMinutes(10);

    public long MaxSegmentBytes { get; set; } = 2L * 1024 * 1024 * 1024;

    public string WorkingDirectory { get; set; } = Path.Combine(Path.GetTempPath(), "socalytics-segments");
}

public sealed class SegmentToolchainOptions
{
    public const string SectionName = "Segments:Toolchain";

    public string FfmpegPath { get; set; } = "ffmpeg";

    public string FfprobePath { get; set; } = "ffprobe";

    // An empty prefix accepts any installed version.
    public string ExpectedVersionPrefix { get; set; } = string.Empty;
}

internal sealed class SegmentMaterializationOptionsValidator : IValidateOptions<SegmentMaterializationOptions>
{
    public ValidateOptionsResult Validate(string? name, SegmentMaterializationOptions options)
    {
        var failures = new List<string>();
        var section = SegmentMaterializationOptions.SectionName;

        if (options.PollInterval <= TimeSpan.Zero)
        {
            failures.Add($"{section}:PollInterval must be positive.");
        }

        if (options.LeaseDuration <= TimeSpan.Zero)
        {
            failures.Add($"{section}:LeaseDuration must be positive.");
        }

        if (options.MaxConcurrentPerInstance < 1)
        {
            failures.Add($"{section}:MaxConcurrentPerInstance must be at least 1.");
        }

        if (options.MaxAttempts < 1)
        {
            failures.Add($"{section}:MaxAttempts must be at least 1.");
        }

        if (options.RetryDelay <= TimeSpan.Zero)
        {
            failures.Add($"{section}:RetryDelay must be positive.");
        }

        if (options.ExecutionTimeout <= TimeSpan.Zero)
        {
            failures.Add($"{section}:ExecutionTimeout must be positive.");
        }

        if (options.MaxSegmentBytes < 1)
        {
            failures.Add($"{section}:MaxSegmentBytes must be at least 1.");
        }

        if (string.IsNullOrWhiteSpace(options.WorkingDirectory))
        {
            failures.Add($"{section}:WorkingDirectory must not be empty.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}

internal sealed class SegmentToolchainOptionsValidator : IValidateOptions<SegmentToolchainOptions>
{
    public ValidateOptionsResult Validate(string? name, SegmentToolchainOptions options)
    {
        var failures = new List<string>();
        var section = SegmentToolchainOptions.SectionName;

        if (string.IsNullOrWhiteSpace(options.FfmpegPath))
        {
            failures.Add($"{section}:FfmpegPath must not be empty.");
        }

        if (string.IsNullOrWhiteSpace(options.FfprobePath))
        {
            failures.Add($"{section}:FfprobePath must not be empty.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
