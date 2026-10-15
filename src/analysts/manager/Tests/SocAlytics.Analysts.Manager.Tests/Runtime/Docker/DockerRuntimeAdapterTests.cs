#pragma warning disable xUnit1051 // The fake handlers answer synchronously; there is nothing to cancel.
using System.Net;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;
using SocAlytics.Analysts.Manager.Hosting;
using SocAlytics.Analysts.Manager.Platform;
using SocAlytics.Analysts.Manager.Runtime;
using SocAlytics.Analysts.Manager.Runtime.Docker;
using SocAlytics.Analysts.Manager.Tests.Support;

namespace SocAlytics.Analysts.Manager.Tests.Runtime.Docker;

public class DockerRuntimeAdapterTests : IDisposable
{
    private const string ProbeImage = "registry.test/socalytics/test-analyst@sha256:" + "a1b2c3d4e5f60718293a4b5c6d7e8f90a1b2c3d4e5f60718293a4b5c6d7e8f90";

    private const string VersionBody = """{"Platform":{"Name":"Docker Desktop 4.40.0 (187762)"},"Version":"28.0.4","ApiVersion":"1.48","MinAPIVersion":"1.24","Os":"linux","Arch":"amd64"}""";
    private const string InfoBody = """{"OSType":"linux","OperatingSystem":"Docker Desktop","NCPU":8,"MemTotal":8589934592}""";

    private const string CapabilitiesBody = """
        {"workflow":{"id":"default","version":"3"},"capabilities":[
          {"id":"ok","version":"1.0.0","accelerators":[],"cpus":"1","memoryBytes":536870912},
          {"id":"gpu","version":"1.0.0","accelerators":["gpu0"],"cpus":"1","memoryBytes":536870912},
          {"id":"big","version":"1.0.0","accelerators":[],"cpus":"64","memoryBytes":536870912},
          {"id":"other","version":"1.0.0","accelerators":[],"cpus":"1","memoryBytes":536870912}]}
        """;

    private sealed class FakeEngine : HttpMessageHandler
    {
        public string Info { get; set; } = InfoBody;
        public string ImageOs { get; set; } = "linux";
        public List<string> Paths { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            Paths.Add(path);
            var body = path switch
            {
                "/version" => VersionBody,
                _ when path.EndsWith("/info", StringComparison.Ordinal) => Info,
                _ when path.EndsWith("/images/create", StringComparison.Ordinal) => """{"status":"Pulling"}""",
                _ when path.EndsWith("/json", StringComparison.Ordinal) => $$"""{"Id":"sha256:abc","Os":"{{ImageOs}}","Architecture":"amd64","Size":1000}""",
                _ => "{}",
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private readonly FakeTimeProvider _time = new(DateTimeOffset.Parse("2026-10-15T09:00:00Z"));
    private readonly FakeEngine _handler = new();
    private readonly FakePlatform _platform;
    private readonly DockerEngineClient _engine;
    private readonly RegistryCredentialProvider _credentials;
    private readonly PlatformClient _client;
    private readonly PlatformSession _session;
    private readonly List<string> _files = [];

    public DockerRuntimeAdapterTests()
    {
        _engine = new DockerEngineClient(new HttpClient(_handler) { BaseAddress = new Uri("http://localhost") });
        _platform = new FakePlatform(_time);
        _platform.Script(WorkOperation.WorkflowCapabilities, HttpStatusCode.OK, CapabilitiesBody);
        _client = new PlatformClient(new HttpClient(_platform), new ProofFactory(_time));
        _credentials = new RegistryCredentialProvider(_client, _time);
        _session = new PlatformSession(new Uri("https://platform.test"), new InMemoryDeviceKeyProvider().ImportFixtureKey(), "opaque");
    }

    public void Dispose()
    {
        _engine.Dispose();
        _credentials.Dispose();
        foreach (var file in _files)
        {
            File.Delete(file);
        }
    }

    private DockerRuntimeAdapter Adapter(DockerRuntimeOptions? options = null) => new(
        _engine, new DockerEndpointDecision(true, DockerEndpointKind.UnixSocket, "/var/run/docker.sock", null, null),
        options ?? new DockerRuntimeOptions { Cpus = 2, MemoryBytes = 1024L * 1024 * 1024 }, _credentials, _client,
        _ => Task.FromResult<PlatformSession?>(_session), () => "manager-1");

    private static RuntimeProbeRequest Request => new(ProbeImage);

    private static RuntimeCompatibilityList List(RuntimeProbe probe, string versionRange = ">=28.0.0 <29.0.0") => new(
    [
        new RuntimeCompatibilityEntry(DockerRuntimeAdapter.ProfileId, "Docker Desktop", versionRange, "linux", probe.Os!, "*",
            probe.Architecture!, [], "*"),
    ]);

    private static PreflightRunner Runner(DockerRuntimeAdapter adapter, RuntimeCompatibilityList list) =>
        new(new RuntimeAdapterCatalog([adapter]), list,
            Options.Create(new RuntimeOptions { Profile = DockerRuntimeAdapter.ProfileId, ProbeImage = ProbeImage }), _timeProvider);

    private static readonly FakeTimeProvider _timeProvider = new();

    [Fact]
    public async Task Probe_maps_the_recorded_version_and_info_bodies()
    {
        var probe = await Adapter().ProbeAsync(Request, TestContext.Current.CancellationToken);

        probe.RuntimeProduct.ShouldBe("Docker Desktop");
        probe.RuntimeVersion.ShouldBe("28.0.4");
        probe.Backend.ShouldBe("linux");
        probe.Accelerators.ShouldBeEmpty();
        probe.EndpointReachable.Passed.ShouldBeTrue();
        probe.BackendAvailable.Passed.ShouldBeTrue();
        probe.RegistryAuthentication.Passed.ShouldBeTrue();
        probe.ImagePlatformCompatible.Passed.ShouldBeTrue();
        probe.ResourceLimitsFeasible.Passed.ShouldBeTrue();
        _handler.Paths.ShouldContain(p => p.EndsWith("/images/create", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Probe_fails_the_checks_for_a_windows_backend_a_foreign_image_and_oversize_limits()
    {
        _handler.Info = """{"OSType":"windows","NCPU":1,"MemTotal":1073741824}""";
        _handler.ImageOs = "linux";

        var probe = await Adapter().ProbeAsync(Request, TestContext.Current.CancellationToken);

        probe.BackendAvailable.Passed.ShouldBeFalse();
        probe.ImagePlatformCompatible.Passed.ShouldBeFalse();
        probe.ResourceLimitsFeasible.Passed.ShouldBeFalse();
    }

    [Fact]
    public async Task A_matching_compatibility_entry_passes_and_a_non_matching_one_fails_the_version_check()
    {
        var adapter = Adapter();
        var probe = await adapter.ProbeAsync(Request, TestContext.Current.CancellationToken);

        var supported = await Runner(adapter, List(probe)).RunAsync(TestContext.Current.CancellationToken);
        var unsupported = await Runner(adapter, List(probe, ">=29.0.0")).RunAsync(TestContext.Current.CancellationToken);

        supported.Passed.ShouldBeTrue();
        supported.MaxParallelAttempts.ShouldBe(2);
        unsupported.Passed.ShouldBeFalse();
        unsupported.Checks.Single(c => !c.Passed).Id.ShouldBe("runtime-version-supported");
        unsupported.ValidatedCapabilities.ShouldBeEmpty();
    }

    [Fact]
    public async Task Offer_excludes_accelerator_oversize_and_non_allowlisted_capabilities_with_reasons()
    {
        var options = new DockerRuntimeOptions
        {
            Cpus = 2, MemoryBytes = 1024L * 1024 * 1024,
            CapabilityAllowlist = ["ok@1.0.0", "gpu@1.0.0", "big@1.0.0"],
        };
        var adapter = Adapter(options);

        await adapter.ProbeAsync(Request, TestContext.Current.CancellationToken);

        adapter.Capabilities.ShouldBe([new QueueCapability("ok", "1.0.0")]);
        adapter.NotOffered.Select(n => (n.Id, n.Reason)).ShouldBe(
        [
            ("gpu", DockerRuntimeAdapter.AcceleratorRequired),
            ("big", DockerRuntimeAdapter.ExceedsHostLimits),
            ("other", DockerRuntimeAdapter.ExcludedByAllowlist),
        ]);
    }

    [Fact]
    public async Task Offer_is_empty_while_no_pull_credential_is_available()
    {
        _platform.Script(WorkOperation.RegistryCredentials, HttpStatusCode.Conflict, "{}");
        var adapter = Adapter();

        var probe = await adapter.ProbeAsync(Request, TestContext.Current.CancellationToken);

        adapter.Capabilities.ShouldBeEmpty();
        probe.RegistryAuthentication.Passed.ShouldBeFalse();
    }

    [Fact]
    public async Task Start_attempt_returns_a_docker_execution_pinned_to_the_digest()
    {
        var adapter = Adapter();
        await adapter.ProbeAsync(Request, TestContext.Current.CancellationToken);
        var launch = new AttemptLaunch(
            "attempt-1", "job-1", "test-analyst", "cap:1", "scope", "sha-256:p", "sha-256:" + new string('a', 64), string.Empty,
            new AttemptResources(1, 256L * 1024 * 1024, []));

        var execution = await adapter.StartAttemptAsync(launch, TestContext.Current.CancellationToken);

        execution.ShouldBeOfType<DockerAttemptExecution>();
        DockerRuntimeAdapter.ImageReference(_credentials.Current, launch)
            .ShouldBe("registry.test/socalytics/test-analyst@sha256:" + new string('a', 64));
        await execution.StopAsync(CancellationToken.None);
    }

    [Fact]
    public void Compatibility_file_adds_entries_in_Development_and_Test_and_is_refused_elsewhere()
    {
        var path = Path.Combine(Path.GetTempPath(), "compat-" + Guid.NewGuid().ToString("N") + ".json");
        _files.Add(path);
        File.WriteAllText(path, """
            [{"profile":"docker-desktop","runtimeProduct":"Docker Desktop","runtimeVersionRange":">=28.0.0","backend":"linux",
              "os":"linux","osVersionRange":"*","architecture":"x64","accelerators":[],"managerVersionRange":"*"}]
            """);

        var embedded = RuntimeCompatibilityList.LoadEmbedded().Entries.Count;
        RuntimeCompatibilityList.Load(path, true).Entries.Count.ShouldBe(embedded + 1);
        RuntimeCompatibilityList.Load(null, false).Entries.Count.ShouldBe(embedded);
        Should.Throw<InvalidOperationException>(() => RuntimeCompatibilityList.Load(path, false))
            .Message.ShouldContain("Development and Test");
    }
}
