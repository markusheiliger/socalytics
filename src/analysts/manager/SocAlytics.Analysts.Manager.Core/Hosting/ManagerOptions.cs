namespace SocAlytics.Analysts.Manager.Hosting;

public enum DrainTimeoutPolicy
{
    CancelRequest,
    StopAndCleanUp,
}

public sealed class ManagerOptions
{
    public const string SectionName = "Manager";

    public string? StateDirectory { get; set; }
}

public sealed class Pkcs11Options
{
    public const string SectionName = "KeyStore:Pkcs11";

    public string ModulePath { get; set; } = "/usr/lib/x86_64-linux-gnu/pkcs11/libtpm2_pkcs11.so";

    public string TokenLabel { get; set; } = "socalytics-device";
}

public sealed class RetryBackoffOptions
{
    public TimeSpan Initial { get; set; } = TimeSpan.FromSeconds(2);

    public TimeSpan Max { get; set; } = TimeSpan.FromSeconds(60);
}

public sealed class PlatformOptions
{
    public const string SectionName = "Platform";

    public TimeSpan StatusCheckInterval { get; set; } = TimeSpan.FromSeconds(60);

    public RetryBackoffOptions RetryBackoff { get; set; } = new();
}

public sealed class OperatingOptions
{
    public const string SectionName = "Operating";

    public TimeSpan DrainTimeout { get; set; } = TimeSpan.FromMinutes(5);

    public DrainTimeoutPolicy DrainTimeoutPolicy { get; set; } = DrainTimeoutPolicy.CancelRequest;

    public TimeSpan CleanupBound { get; set; } = TimeSpan.FromSeconds(30);

    public TimeSpan SessionEndDrainBound { get; set; } = TimeSpan.FromSeconds(5);

    public TimeSpan MaxTimedPause { get; set; } = TimeSpan.FromDays(7);
}

public sealed class RuntimeOptions
{
    public const string SectionName = "Runtime";

    public string? Profile { get; set; }

    public TimeSpan PreflightInterval { get; set; } = TimeSpan.FromSeconds(60);

    public string? ProbeImage { get; set; }
}
