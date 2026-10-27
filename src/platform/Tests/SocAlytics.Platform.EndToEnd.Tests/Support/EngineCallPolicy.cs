namespace SocAlytics.Platform.EndToEnd.Tests.Support;

/// <summary>The named limits and retry values of the Docker engine and registry calls of the end-to-end tests (development values).</summary>
internal static class EngineCallPolicy
{
    /// <summary>Time limit of one push attempt.</summary>
    public static readonly TimeSpan PushLimit = TimeSpan.FromMinutes(15);

    /// <summary>Time limit of one image build attempt.</summary>
    public static readonly TimeSpan BuildLimit = TimeSpan.FromMinutes(20);

    /// <summary>Time limit of one registry manifest lookup or engine image-record lookup.</summary>
    public static readonly TimeSpan RegistryLookupLimit = TimeSpan.FromSeconds(30);

    /// <summary>Attempts of a push or build including the first one.</summary>
    public const int MaxAttempts = 3;

    /// <summary>Bounded delay between two attempts of a push or build.</summary>
    public static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(2);
}
