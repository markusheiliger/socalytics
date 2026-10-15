namespace SocAlytics.Analysts.Manager.Runtime;

public sealed record ProbeCheck(bool Passed, string? Detail = null, string? Remediation = null);

public sealed record AcceleratorProbe(string Id, bool Passed, string? Detail = null, string? Remediation = null);

public sealed record RuntimeProbeRequest(string? ProbeImage);

/// <summary>Raw facts an adapter observed; the runner alone decides support from the compatibility list.</summary>
public sealed record RuntimeProbe(
    string? RuntimeProduct,
    string? RuntimeVersion,
    string? Backend,
    string? Os,
    string? OsVersion,
    string? Architecture,
    ProbeCheck EndpointReachable,
    ProbeCheck EndpointLocal,
    ProbeCheck EndpointAuthorized,
    ProbeCheck ProfileVersionSupported,
    ProbeCheck BackendAvailable,
    ProbeCheck RegistryAuthentication,
    ProbeCheck ImagePlatformCompatible,
    ProbeCheck ResourceLimitsFeasible,
    IReadOnlyList<AcceleratorProbe> Accelerators,
    int MaxParallelAttempts = 1,
    string? DaemonAccess = null);

/// <summary>How the Manager's account reaches the container daemon (<see cref="RuntimeProbe.DaemonAccess"/>).</summary>
public static class DaemonAccessKinds
{
    public const string Rootless = "rootless";
    public const string RootfulGroup = "rootful-group";
}

public interface IContainerRuntimeAdapter
{
    string Profile { get; }

    /// <summary>The analyst capabilities this runtime offers to the queue once its preflight passed.</summary>
    IReadOnlyList<Platform.QueueCapability> Capabilities => [];

    Task<RuntimeProbe> ProbeAsync(RuntimeProbeRequest request, CancellationToken cancellationToken);

    Task<IAttemptExecution> StartAttemptAsync(AttemptLaunch launch, CancellationToken cancellationToken);
}
