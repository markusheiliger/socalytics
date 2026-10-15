using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SocAlytics.Analysts.Manager.Platform;

namespace SocAlytics.Analysts.Manager.Runtime.Docker;

public sealed record NotOfferedCapability(string Id, string Version, string Reason);

/// <summary>
/// The <c>docker-desktop</c> runtime profile: probes the local Engine API, offers the workflow capabilities this host can run,
/// and starts one <see cref="DockerAttemptExecution"/> per attempt. Whether the runtime is supported is decided only by the compatibility list.
/// </summary>
public sealed class DockerRuntimeAdapter : IContainerRuntimeAdapter
{
    public const string ProfileId = "docker-desktop";
    public const string AcceleratorRequired = "accelerator-required";
    public const string ExceedsHostLimits = "exceeds-host-limits";
    public const string ExcludedByAllowlist = "excluded-by-allowlist";

    private readonly DockerEngineClient? _engine;
    private readonly DockerEndpointDecision _endpoint;
    private readonly DockerRuntimeOptions _options;
    private readonly RegistryCredentialProvider _credentials;
    private readonly PlatformClient _platform;
    private readonly Func<CancellationToken, Task<PlatformSession?>> _session;
    private readonly Func<string?> _managerId;
    private readonly ILogger _logger;
    private volatile IReadOnlyList<Platform.QueueCapability> _offered = [];
    private volatile IReadOnlyList<NotOfferedCapability> _notOffered = [];

    public DockerRuntimeAdapter(
        DockerEngineClient? engine, DockerEndpointDecision endpoint, DockerRuntimeOptions options,
        RegistryCredentialProvider credentials, PlatformClient platform,
        Func<CancellationToken, Task<PlatformSession?>> session, Func<string?> managerId, ILogger? logger = null)
    {
        _engine = engine;
        _endpoint = endpoint;
        _options = options;
        _credentials = credentials;
        _platform = platform;
        _session = session;
        _managerId = managerId;
        _logger = logger ?? NullLogger.Instance;
    }

    public string Profile => ProfileId;

    /// <summary>The capabilities of the last probe; empty while no valid pull credential is available, so no work that needs a pull is claimed.</summary>
    public IReadOnlyList<Platform.QueueCapability> Capabilities => _credentials.Current is null ? [] : _offered;

    /// <summary>Each workflow capability of the last probe that is not offered, with its reason.</summary>
    public IReadOnlyList<NotOfferedCapability> NotOffered => _notOffered;

    public async Task<RuntimeProbe> ProbeAsync(RuntimeProbeRequest request, CancellationToken cancellationToken)
    {
        _offered = [];
        _notOffered = [];
        var ok = new ProbeCheck(true);
        if (!_endpoint.Allowed || _engine is null)
        {
            var refused = new ProbeCheck(false, _endpoint.Reason, _endpoint.Remediation);
            return Failed(ok, refused);
        }

        DockerVersionInfo version;
        DockerInfo info;
        try
        {
            version = await _engine.GetVersionAsync(cancellationToken);
            info = await _engine.GetInfoAsync(cancellationToken);
        }
        catch (DockerEngineException e) when (e.Kind == DockerErrorKind.UnsupportedVersion)
        {
            return Failed(ok, ok, versionSupported: new ProbeCheck(false, e.Message));
        }
        catch (DockerEngineException e) when (e.Kind == DockerErrorKind.Unauthorized)
        {
            return Failed(ok, ok, authorized: new ProbeCheck(false, e.Message));
        }
        catch (Exception e) when (e is DockerEngineException or HttpRequestException or IOException)
        {
            return Failed(new ProbeCheck(false, e.GetType().Name), ok);
        }

        var backend = info.OsType;
        var backendCheck = string.Equals(backend, "linux", StringComparison.OrdinalIgnoreCase)
            ? ok
            : new ProbeCheck(false, $"The runtime runs {backend ?? "unknown"} containers.");

        var cpus = _options.EffectiveCpus;
        var memory = _options.EffectiveMemoryBytes(info.MemTotal);
        var resources = cpus <= info.Ncpu && memory <= info.MemTotal && info.Ncpu > 0 && info.MemTotal > 0
            ? ok
            : new ProbeCheck(false, $"Configured {cpus} CPUs and {memory} bytes of memory; the runtime has {info.Ncpu} CPUs and {info.MemTotal} bytes.");

        var session = await _session(cancellationToken);
        try
        {
            await _credentials.StepAsync(() => session, cancellationToken);
        }
        catch (Exception e) when (e is HttpRequestException or PlatformException)
        {
            _logger.LogWarning("Requesting the registry pull credential failed: {Error}", e.GetType().Name);
        }

        var credential = _credentials.Current;
        ProbeCheck registry;
        ProbeCheck platformCheck = ok;
        if (credential is null)
        {
            registry = new ProbeCheck(false, "No valid registry pull credential is available from the platform.");
        }
        else
        {
            registry = ok;
            if (!string.IsNullOrWhiteSpace(request.ProbeImage))
            {
                (registry, platformCheck) = await ProbeImageAsync(request.ProbeImage, credential, info, version, cancellationToken);
            }
        }

        if (session is not null)
        {
            await RefreshOfferAsync(session, cpus, memory, cancellationToken);
        }

        return new RuntimeProbe(
            NormalizeProduct(version.PlatformName), version.Version, backend, HostOs(), Environment.OSVersion.Version.ToString(), HostArchitecture(),
            ok, ok, ok, ok, backendCheck, registry, platformCheck, resources, [], MaxParallel(cpus, memory));
    }

    public Task<IAttemptExecution> StartAttemptAsync(AttemptLaunch launch, CancellationToken cancellationToken)
    {
        if (_engine is null)
        {
            throw new InvalidOperationException("The runtime endpoint is not allowed.");
        }

        var credential = _credentials.Current;
        var reference = ImageReference(credential, launch);
        IAttemptExecution execution = new DockerAttemptExecution(
            _engine, _credentials, launch, reference, _managerId() ?? string.Empty, DockerLaunchSpec.DefaultUser(), _logger);
        return Task.FromResult(execution);
    }

    /// <summary>
    /// The pinned digest inside the credential's repository for the capability: the repository named like the capability,
    /// or the only one. Without a match the reference lies outside the credential and the attempt fails as image-unavailable before any pull.
    /// </summary>
    internal static string ImageReference(RegistryPullCredential? credential, AttemptLaunch launch)
    {
        var digest = launch.ImageDigest.StartsWith("sha-256:", StringComparison.Ordinal)
            ? "sha256:" + launch.ImageDigest["sha-256:".Length..]
            : launch.ImageDigest;
        if (credential is null)
        {
            return $"unavailable/{launch.CapabilityId}@{digest}";
        }

        var repository = credential.Repositories.FirstOrDefault(r =>
                string.Equals(r[(r.LastIndexOf('/') + 1)..], launch.CapabilityId, StringComparison.Ordinal))
            ?? (credential.Repositories.Count == 1 ? credential.Repositories[0] : null);
        return repository is null
            ? $"{credential.Registry}/unresolved/{launch.CapabilityId}@{digest}"
            : $"{credential.Registry}/{repository}@{digest}";
    }

    private async Task<(ProbeCheck Registry, ProbeCheck Platform)> ProbeImageAsync(
        string image, RegistryPullCredential credential, DockerInfo info, DockerVersionInfo version, CancellationToken cancellationToken)
    {
        if (!DockerAttemptExecution.IsInsidePrefix(image, credential))
        {
            return (new ProbeCheck(false, "The probe image lies outside the registry and repositories the platform issued a credential for."), new ProbeCheck(true));
        }

        try
        {
            await _engine!.PullImageAsync(image, credential.AuthToken, cancellationToken);
            var inspected = await _engine.InspectImageAsync(image, cancellationToken);
            if (inspected is null)
            {
                return (new ProbeCheck(true), new ProbeCheck(false, "The probe image is not present after the pull."));
            }

            var matches = string.Equals(inspected.Os, info.OsType, StringComparison.OrdinalIgnoreCase)
                && string.Equals(inspected.Architecture, version.Arch, StringComparison.OrdinalIgnoreCase);
            return (new ProbeCheck(true), matches
                ? new ProbeCheck(true)
                : new ProbeCheck(false, $"The probe image is {inspected.Os}/{inspected.Architecture}; the runtime is {info.OsType}/{version.Arch}."));
        }
        catch (DockerEngineException e) when (e.Kind is DockerErrorKind.Unauthorized or DockerErrorKind.NotFound or DockerErrorKind.Failed)
        {
            return (new ProbeCheck(false, $"The probe image could not be pulled: {e.Kind}."), new ProbeCheck(true));
        }
    }

    private async Task RefreshOfferAsync(PlatformSession session, decimal cpus, long memory, CancellationToken cancellationToken)
    {
        WorkResult<WorkflowCapabilitiesResponse> result;
        try
        {
            result = await _platform.GetWorkflowCapabilitiesAsync(session, cancellationToken);
        }
        catch (Exception e) when (e is HttpRequestException or PlatformException)
        {
            _logger.LogWarning("Reading the workflow capabilities failed: {Error}", e.GetType().Name);
            return;
        }

        if (result is not { Kind: WorkResultKind.Success, Value: { } response })
        {
            return;
        }

        var offered = new List<Platform.QueueCapability>();
        var notOffered = new List<NotOfferedCapability>();
        foreach (var capability in response.Capabilities)
        {
            var requiredCpus = decimal.TryParse(capability.Cpus, NumberStyles.Number, CultureInfo.InvariantCulture, out var c) ? c : decimal.MaxValue;
            string? reason = null;
            if (capability.Accelerators.Count > 0)
            {
                reason = AcceleratorRequired;
            }
            else if (requiredCpus > cpus || capability.MemoryBytes > memory)
            {
                reason = ExceedsHostLimits;
            }
            else if (_options.CapabilityAllowlist.Count > 0
                && !_options.CapabilityAllowlist.Contains($"{capability.Id}@{capability.Version}", StringComparer.Ordinal))
            {
                reason = ExcludedByAllowlist;
            }

            if (reason is null)
            {
                offered.Add(new Platform.QueueCapability(capability.Id, capability.Version));
                _requirements[(capability.Id, capability.Version)] = (requiredCpus, capability.MemoryBytes);
            }
            else
            {
                notOffered.Add(new NotOfferedCapability(capability.Id, capability.Version, reason));
            }
        }

        _offered = offered;
        _notOffered = notOffered;
    }

    private readonly Dictionary<(string, string), (decimal Cpus, long Memory)> _requirements = [];

    /// <summary>The number of attempts of the lightest offered capability that fit the configured limits; at least one.</summary>
    private int MaxParallel(decimal cpus, long memory)
    {
        var fits = _offered
            .Select(o => _requirements.TryGetValue((o.Id, o.Version), out var r) ? r : default)
            .Where(r => r.Cpus > 0 && r.Memory > 0)
            .Select(r => (int)Math.Min(Math.Floor(cpus / r.Cpus), memory / r.Memory))
            .DefaultIfEmpty(1);
        return Math.Max(1, fits.Max());
    }

    private static RuntimeProbe Failed(ProbeCheck reachable, ProbeCheck local, ProbeCheck? authorized = null, ProbeCheck? versionSupported = null)
    {
        var ok = new ProbeCheck(true);
        var skipped = new ProbeCheck(false, "Not checked because the runtime could not be queried.");
        return new RuntimeProbe(
            null, null, null, HostOs(), Environment.OSVersion.Version.ToString(), HostArchitecture(),
            reachable, local, authorized ?? ok, versionSupported ?? ok, skipped, skipped, skipped, skipped, [], 0);
    }

    private static string NormalizeProduct(string? platformName)
    {
        if (string.IsNullOrWhiteSpace(platformName))
        {
            return "Docker";
        }

        foreach (var product in new[] { "Docker Desktop", "Docker Engine" })
        {
            if (platformName.StartsWith(product, StringComparison.OrdinalIgnoreCase))
            {
                return product;
            }
        }

        return platformName.Trim();
    }

    private static string HostOs() =>
        OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "linux";

    private static string HostArchitecture() => RuntimeInformation.OSArchitecture switch
    {
        Architecture.X64 => "x64",
        Architecture.Arm64 => "arm64",
        var other => other.ToString().ToLowerInvariant(),
    };
}
