using Microsoft.Extensions.Options;
using SocAlytics.Analysts.Manager.Hosting;

namespace SocAlytics.Analysts.Manager.Runtime;

public sealed class PreflightRunner
{
    private readonly RuntimeAdapterCatalog catalog;
    private readonly RuntimeCompatibilityList compatibility;
    private readonly IOptions<RuntimeOptions> options;
    private readonly TimeProvider time;
    private readonly ApplicationProfile profile;
    private readonly AccountShell accountShell;
    private readonly Docker.DockerReconciliationService? reconciliation;

    public PreflightRunner(
        RuntimeAdapterCatalog catalog,
        RuntimeCompatibilityList compatibility,
        IOptions<RuntimeOptions> options,
        TimeProvider time,
        ApplicationProfile profile = ApplicationProfile.Tray,
        AccountShell? accountShell = null,
        Docker.DockerReconciliationService? reconciliation = null)
    {
        this.reconciliation = reconciliation;
        this.profile = profile;
        this.accountShell = accountShell ?? new AccountShell();
        this.catalog = catalog;
        this.compatibility = compatibility;
        this.options = options;
        this.time = time;
    }

    public async Task<PreflightResult> RunAsync(CancellationToken cancellationToken)
    {
        var result = await RunChecksAsync(cancellationToken);
        if (reconciliation is null || result.Profile != Docker.DockerRuntimeAdapter.ProfileId)
        {
            return result;
        }

        return result with { Reconciliation = await reconciliation.ReconcileAsync(cancellationToken) };
    }

    private async Task<PreflightResult> RunChecksAsync(CancellationToken cancellationToken)
    {
        var profile = options.Value.Profile;
        var adapter = catalog.Find(profile);
        if (adapter is null)
        {
            var detail = string.IsNullOrWhiteSpace(profile)
                ? "No runtime profile is configured."
                : $"No built-in runtime adapter exists for profile '{profile}'.";
            return new PreflightResult(time.GetUtcNow(), profile, null, null, null, false,
                [new PreflightCheck("runtime-integration-configured", false, detail,
                    "Set Runtime:Profile to a validated runtime profile that this Manager version supports.")],
                []);
        }

        RuntimeProbe probe;
        try
        {
            probe = await adapter.ProbeAsync(new RuntimeProbeRequest(options.Value.ProbeImage), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new PreflightResult(time.GetUtcNow(), adapter.Profile, null, null, null, false,
                [
                    new PreflightCheck("runtime-integration-configured", true, null, null),
                    new PreflightCheck("endpoint-reachable", false, ex.GetType().Name,
                        "Make sure the container runtime is installed and running."),
                ],
                []);
        }

        var managerVersion = typeof(PreflightRunner).Assembly.GetName().Version ?? new Version();
        var supported = compatibility.Match(adapter.Profile, probe, managerVersion);
        var checks = new List<PreflightCheck>
        {
            new("runtime-integration-configured", true, null, null),
            From("endpoint-reachable", probe.EndpointReachable, "Start the container runtime and check its endpoint."),
            From("endpoint-local", probe.EndpointLocal, "Point the runtime at a local endpoint; remote endpoints are refused."),
            From("endpoint-authorized", probe.EndpointAuthorized, "Grant this operating-system account access to the runtime endpoint."),
            supported is not null
                ? new PreflightCheck("runtime-version-supported", true, $"{probe.RuntimeProduct} {probe.RuntimeVersion}", null)
                : new PreflightCheck("runtime-version-supported", false,
                    $"{probe.RuntimeProduct} {probe.RuntimeVersion} on {probe.Backend}/{probe.Os}/{probe.Architecture} is not in the compatibility list.",
                    SupportedRuntimes(adapter.Profile)),
            From("profile-version-supported", probe.ProfileVersionSupported, "Use a runtime version supported by the selected profile."),
            From("backend-available", probe.BackendAvailable, "Enable the container backend the runtime requires."),
            From("registry-authentication", probe.RegistryAuthentication, "Sign in to the container registry."),
            From("image-platform-compatible", probe.ImagePlatformCompatible, "Use a runtime that can run the probe image platform."),
            From("resource-limits-feasible", probe.ResourceLimitsFeasible, "Make sure the runtime can enforce CPU and memory limits."),
        };
        if (this.profile == ApplicationProfile.HeadlessServer)
        {
            checks.Add(AccountIsolation(probe));
        }

        foreach (var accelerator in probe.Accelerators)
        {
            checks.Add(new PreflightCheck($"accelerator-probe:{accelerator.Id}", accelerator.Passed, accelerator.Detail,
                accelerator.Passed ? null : accelerator.Remediation ?? "Fix the accelerator driver or runtime integration."));
        }

        var passed = checks.All(c => c.Passed);
        var capabilities = passed
            ? probe.Accelerators.Where(a => a.Passed).Select(a => a.Id).ToArray()
            : [];
        return new PreflightResult(time.GetUtcNow(), adapter.Profile, probe.RuntimeProduct, probe.RuntimeVersion,
            probe.Backend, passed, checks, capabilities, passed ? probe.MaxParallelAttempts : 0)
        {
            Offered = passed ? [.. adapter.Capabilities.Select(c => new PreflightCapability(c.Id, c.Version))] : [],
            NotOffered = passed ? [.. adapter.NotOffered.Select(n => new PreflightCapability(n.Id, n.Version, n.Reason))] : [],
        };
    }

    private string SupportedRuntimes(string profile)
    {
        var ranges = compatibility.Entries
            .Where(e => e.Profile == profile)
            .Select(e => $"{e.RuntimeProduct} {e.RuntimeVersionRange} ({e.Backend} containers on {e.Os}/{e.Architecture})")
            .ToArray();
        return ranges.Length == 0
            ? "Install a runtime version that this Manager version lists as compatible; it lists none for this profile."
            : $"Update the runtime to a supported version: {string.Join("; ", ranges)}.";
    }

    private PreflightCheck AccountIsolation(RuntimeProbe probe) =>
        probe.DaemonAccess == DaemonAccessKinds.RootfulGroup && accountShell.IsLoginCapable()
            ? new PreflightCheck("account-isolation", false,
                "A rootful container runtime reached through group membership requires a dedicated non-login service account.",
                "Run the Manager under a dedicated account without a login shell, or use a rootless runtime (see the Manager README, Headless Server Profile).")
            : new PreflightCheck("account-isolation", true, null, null);

    private static PreflightCheck From(string id, ProbeCheck check, string remediation) =>
        new(id, check.Passed, check.Detail, check.Passed ? null : check.Remediation ?? remediation);
}
