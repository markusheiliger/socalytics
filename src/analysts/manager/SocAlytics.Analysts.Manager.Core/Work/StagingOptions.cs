namespace SocAlytics.Analysts.Manager.Work;

/// <summary>Bound from <c>Work:Staging</c>.</summary>
public sealed class StagingOptions
{
    public const string SectionName = "Work:Staging";
    public const long MinimumBytes = 1024 * 1024;

    /// <summary>The most upstream bytes one attempt may stage; the development value is 2 GiB.</summary>
    public long MaxBytesPerAttempt { get; set; } = 2147483648;

    public void Validate()
    {
        if (MaxBytesPerAttempt < MinimumBytes)
        {
            throw new InvalidOperationException($"{SectionName}:MaxBytesPerAttempt must be at least 1 MiB.");
        }
    }
}
