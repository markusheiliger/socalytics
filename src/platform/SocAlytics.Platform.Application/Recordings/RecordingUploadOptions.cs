using Microsoft.Extensions.Options;

namespace SocAlytics.Platform.Application.Recordings;

public sealed class RecordingUploadOptions
{
    public const string SectionName = "Recordings:Upload";

    public TimeSpan SessionLifetime { get; set; }

    public TimeSpan GrantLifetime { get; set; }

    public TimeSpan ExpirySweepInterval { get; set; }

    public long MaxObjectSizeBytes { get; set; }

    public long MinPartSizeBytes { get; set; }

    public long MaxPartSizeBytes { get; set; }

    public int MaxPartCount { get; set; }

    public int MaxGrantsPerRequest { get; set; }

    public string[] AllowedContentTypes { get; set; } = [];

    public string KeyPrefix { get; set; } = "recordings/";
}

internal sealed class RecordingUploadOptionsValidator : IValidateOptions<RecordingUploadOptions>
{
    private const long StorageMaxObjectSize = 5_497_558_138_880;
    private const long StorageMinPartSize = 5_242_880;
    private const long StorageMaxPartSize = 5_368_709_120;

    public ValidateOptionsResult Validate(string? name, RecordingUploadOptions options)
    {
        var failures = new List<string>();
        var section = RecordingUploadOptions.SectionName;

        if (options.SessionLifetime <= TimeSpan.Zero)
        {
            failures.Add($"{section}:SessionLifetime must be positive.");
        }

        if (options.GrantLifetime <= TimeSpan.Zero || options.GrantLifetime > TimeSpan.FromDays(7))
        {
            failures.Add($"{section}:GrantLifetime must be positive and at most 7 days.");
        }

        if (options.ExpirySweepInterval <= TimeSpan.Zero)
        {
            failures.Add($"{section}:ExpirySweepInterval must be positive.");
        }

        if (options.MaxObjectSizeBytes < 1 || options.MaxObjectSizeBytes > StorageMaxObjectSize)
        {
            failures.Add($"{section}:MaxObjectSizeBytes must be between 1 and {StorageMaxObjectSize}.");
        }

        if (options.MinPartSizeBytes < StorageMinPartSize)
        {
            failures.Add($"{section}:MinPartSizeBytes must be at least {StorageMinPartSize}.");
        }

        if (options.MaxPartSizeBytes > StorageMaxPartSize)
        {
            failures.Add($"{section}:MaxPartSizeBytes must be at most {StorageMaxPartSize}.");
        }

        if (options.MinPartSizeBytes > options.MaxPartSizeBytes)
        {
            failures.Add($"{section}:MinPartSizeBytes must not exceed MaxPartSizeBytes.");
        }

        if (options.MaxPartCount is < 1 or > 10_000)
        {
            failures.Add($"{section}:MaxPartCount must be between 1 and 10000.");
        }

        if (options.MaxGrantsPerRequest is < 1 or > 1_000)
        {
            failures.Add($"{section}:MaxGrantsPerRequest must be between 1 and 1000.");
        }

        if (options.AllowedContentTypes is null
            || options.AllowedContentTypes.Length == 0
            || options.AllowedContentTypes.Any(string.IsNullOrWhiteSpace))
        {
            failures.Add($"{section}:AllowedContentTypes must contain at least one non-empty content type.");
        }

        if (string.IsNullOrWhiteSpace(options.KeyPrefix))
        {
            failures.Add($"{section}:KeyPrefix must not be empty.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
