using Microsoft.Extensions.Options;

namespace SocAlytics.Platform.Application.Recordings;

public sealed class RecordingSetOptions
{
    public const string SectionName = "Recordings:Sets";

    public int MaxMembers { get; set; } = 100;
}

internal sealed class RecordingSetOptionsValidator : IValidateOptions<RecordingSetOptions>
{
    public ValidateOptionsResult Validate(string? name, RecordingSetOptions options) =>
        options.MaxMembers is < 1 or > 1_000
            ? ValidateOptionsResult.Fail($"{RecordingSetOptions.SectionName}:MaxMembers must be between 1 and 1000.")
            : ValidateOptionsResult.Success;
}
