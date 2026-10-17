#pragma warning disable xUnit1051 // The fake handlers answer synchronously; there is nothing to cancel.
using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;
using SocAlytics.Analysts.Manager.Hosting;
using SocAlytics.Analysts.Manager.LocalState;
using SocAlytics.Analysts.Manager.Operating;
using SocAlytics.Analysts.Manager.Platform;
using SocAlytics.Analysts.Manager.Registration;
using SocAlytics.Analysts.Manager.Runtime;
using SocAlytics.Analysts.Manager.Runtime.Docker;
using SocAlytics.Analysts.Manager.Tests.Support;

namespace SocAlytics.Analysts.Manager.Tests.Runtime.Docker;

public class DockerPreflightTests : IDisposable
{
    private const string ProbeImage = "registry.test/socalytics/test-analyst@sha256:" + "a1b2c3d4e5f60718293a4b5c6d7e8f90a1b2c3d4e5f60718293a4b5c6d7e8f90";
    private const string VersionBody = """{"Platform":{"Name":"Docker Desktop 4.40.0 (187762)"},"Version":"28.0.4","ApiVersion":"1.48","MinAPIVersion":"1.24","Os":"linux","Arch":"amd64"}""";
    private const string OldVersionBody = """{"Platform":{"Name":"Docker Desktop 4.10.0"},"Version":"20.10.0","ApiVersion":"1.30","MinAPIVersion":"1.12","Os":"linux","Arch":"amd64"}""";
    private const string InfoBody = """{"OSType":"linux","OperatingSystem":"Docker Desktop","NCPU":8,"MemTotal":8589934592}""";
    private const string CapabilitiesBody = """{"workflow":{"id":"default","version":"3"},"capabilities":[{"id":"ok","version":"1.0.0","accelerators":[],"cpus":"1","memoryBytes":536870912}]}""";

    private sealed class FakeEngine : HttpMessageHandler
    {
        public string Version { get; set; } = VersionBody;
        public string Info { get; set; } = InfoBody;
        public string ImageOs { get; set; } = "linux";
        public bool Down { get; set; }
        public HttpStatusCode PullStatus { get; set; } = HttpStatusCode.OK;
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            if (Down)
            {
                throw new HttpRequestException("The engine is not running.");
            }

            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/images/create", StringComparison.Ordinal) && PullStatus != HttpStatusCode.OK)
            {
                return Task.FromResult(new HttpResponseMessage(PullStatus) { Content = new StringContent("""{"message":"denied"}""") });
            }

            var body = path switch
            {
                "/version" => Version,
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

    public DockerPreflightTests()
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
    }

    private static readonly DockerEndpointDecision Local =
        new(true, DockerEndpointKind.UnixSocket, "/var/run/docker.sock", null, null);

    private DockerRuntimeAdapter Adapter(
        DockerRuntimeOptions? options = null, DockerEndpointDecision? endpoint = null, bool runsAsRoot = false, bool withEngine = true) => new(
        withEngine ? _engine : null, endpoint ?? Local,
        options ?? new DockerRuntimeOptions { Cpus = 2, MemoryBytes = 1024L * 1024 * 1024 }, _credentials, _client,
        _ => Task.FromResult<PlatformSession?>(_session), () => "manager-1", null, () => runsAsRoot);

    private static RuntimeCompatibilityList List() => new(
    [
        new RuntimeCompatibilityEntry(DockerRuntimeAdapter.ProfileId, "Docker Desktop", ">=28.0.0 <29.0.0", "linux",
            OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "linux", "*",
            System.Runtime.InteropServices.RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.Arm64 ? "arm64" : "x64",
            [], "*"),
    ]);

    private static PreflightRunner Runner(DockerRuntimeAdapter adapter, ApplicationProfile profile = ApplicationProfile.Tray) =>
        new(new RuntimeAdapterCatalog([adapter]), List(),
            Options.Create(new RuntimeOptions { Profile = DockerRuntimeAdapter.ProfileId, ProbeImage = ProbeImage }), new FakeTimeProvider(), profile);

    private async Task<PreflightCheck> OnlyFailure(DockerRuntimeAdapter adapter, string id)
    {
        var result = await Runner(adapter).RunAsync(TestContext.Current.CancellationToken);
        result.Passed.ShouldBeFalse();
        result.ValidatedCapabilities.ShouldBeEmpty();
        var failed = result.Checks.Where(c => !c.Passed).ToArray();
        failed.Select(c => c.Id).ShouldContain(id);
        var check = failed.Single(c => c.Id == id);
        check.Detail.ShouldNotBeNullOrWhiteSpace();
        check.Remediation.ShouldNotBeNullOrWhiteSpace();
        return check;
    }

    [Fact]
    public async Task A_healthy_runtime_passes_every_check()
    {
        var result = await Runner(Adapter()).RunAsync(TestContext.Current.CancellationToken);

        result.Passed.ShouldBeTrue();
        result.Checks.ShouldAllBe(c => c.Passed);
    }

    [Fact]
    public async Task An_unreachable_endpoint_tells_the_operator_to_start_Docker_Desktop()
    {
        _handler.Down = true;

        var check = await OnlyFailure(Adapter(), "endpoint-reachable");

        check.Remediation!.ShouldContain("Start Docker Desktop");
        check.Detail!.ShouldContain("did not answer");
    }

    [Fact]
    public async Task A_remote_endpoint_fails_the_endpoint_check_and_is_never_connected_to()
    {
        var remote = DockerEndpointPolicy.Evaluate("tcp://10.0.0.5:2375", null);

        var check = await OnlyFailure(Adapter(endpoint: remote, withEngine: false), "endpoint-local");

        check.Remediation!.ShouldContain("local");
        _handler.Requests.ShouldBe(0);
    }

    [Fact]
    public async Task An_unlisted_runtime_fails_the_profile_check_with_detected_and_supported_values()
    {
        _handler.Version = VersionBody.Replace("28.0.4", "30.1.0", StringComparison.Ordinal);

        var check = await OnlyFailure(Adapter(), "runtime-version-supported");

        check.Detail!.ShouldContain("30.1.0");
        check.Detail!.ShouldContain("linux");
        check.Remediation!.ShouldContain(">=28.0.0 <29.0.0");
    }

    [Fact]
    public async Task A_too_old_engine_asks_for_a_supported_Docker_Desktop_version()
    {
        _handler.Version = OldVersionBody;

        var check = await OnlyFailure(Adapter(), "profile-version-supported");

        check.Remediation!.ShouldContain("Update Docker Desktop");
        check.Remediation!.ShouldContain(DockerEngineClient.MinimumApiVersion);
    }

    [Fact]
    public async Task Windows_containers_ask_to_switch_to_Linux_containers()
    {
        _handler.Info = """{"OSType":"windows","NCPU":8,"MemTotal":8589934592}""";

        var check = await OnlyFailure(Adapter(), "backend-available");

        check.Detail!.ShouldContain("windows");
        check.Remediation!.ShouldContain("Linux containers");
    }

    [Fact]
    public async Task A_rejected_registry_access_fails_the_registry_check_with_remediation()
    {
        _handler.PullStatus = HttpStatusCode.Unauthorized;

        var check = await OnlyFailure(Adapter(), "registry-authentication");

        check.Remediation!.ShouldContain("registry");
    }

    [Fact]
    public async Task A_missing_pull_credential_fails_the_registry_check_and_offers_nothing()
    {
        _platform.Script(WorkOperation.RegistryCredentials, HttpStatusCode.Conflict, "{}");
        var adapter = Adapter();

        await OnlyFailure(adapter, "registry-authentication");

        adapter.Capabilities.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_probe_image_of_another_platform_fails_the_image_platform_check()
    {
        _handler.ImageOs = "windows";

        var check = await OnlyFailure(Adapter(), "image-platform-compatible");

        check.Detail!.ShouldContain("windows/amd64");
        check.Detail!.ShouldContain("linux/amd64");
    }

    [Fact]
    public async Task Limits_the_runtime_cannot_provide_name_the_requested_and_available_values()
    {
        var options = new DockerRuntimeOptions { Cpus = 64, MemoryBytes = 1024L * 1024 * 1024 };

        var check = await OnlyFailure(Adapter(options), "resource-limits-feasible");

        check.Detail!.ShouldContain("64");
        check.Detail!.ShouldContain("8589934592");
        check.Remediation!.ShouldContain("at most 8");
        check.Remediation!.ShouldContain("8589934592");
    }

    [Fact]
    public async Task A_Manager_running_as_root_fails_the_profile_check()
    {
        var check = await OnlyFailure(Adapter(runsAsRoot: true), "profile-version-supported");

        check.Detail!.ShouldContain("root");
        check.Remediation!.ShouldContain("non-root");
    }

    private static ManagerTestHost Host(DockerRuntimeAdapter adapter) =>
        new(configure: s =>
        {
            s.RemoveAll<RuntimeAdapterCatalog>();
            s.AddSingleton(new RuntimeAdapterCatalog([adapter]));
            s.RemoveAll<RuntimeCompatibilityList>();
            s.AddSingleton(List());
            s.Configure<RuntimeOptions>(o =>
            {
                o.Profile = DockerRuntimeAdapter.ProfileId;
                o.ProbeImage = ProbeImage;
            });
        });

    private static async Task Restore(ManagerTestHost host)
    {
        var key = await host.Keys.CreateKeyAsync(false, TestContext.Current.CancellationToken);
        host.Platform.Adopt(key.PublicJwk);
        host.Services.GetRequiredService<SignedStateStore>().Write(
            new ProtectedRegistrationState(
                1, "manager-1", "stamp-1", "https://platform.test", key.Reference, key.Protection, key.Thumbprint,
                OperatingIntent.Running, false, host.Time.GetUtcNow()),
            key);
        await host.Services.GetRequiredService<RegistrationRestorer>().RunAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task The_scheduler_keeps_runtime_unavailable_pulls_no_work_and_returns_to_running_on_a_passing_probe()
    {
        var adapter = Adapter();
        await using var host = Host(adapter);
        await Restore(host);
        var scheduler = host.Services.GetRequiredService<PreflightScheduler>();
        var gate = host.Services.GetRequiredService<AdmissionGate>();

        _handler.Down = true;
        await scheduler.CheckAsync(TestContext.Current.CancellationToken);

        host.Control.GetStatus().OperatingState.ShouldBe("runtime-unavailable");
        scheduler.Latest!.Checks.Single(c => !c.Passed && c.Id == "endpoint-reachable").Remediation.ShouldNotBeNullOrEmpty();
        scheduler.Latest.ValidatedCapabilities.ShouldBeEmpty();
        adapter.Capabilities.ShouldBeEmpty();
        gate.TryAdmit().ShouldBeFalse();

        _handler.Down = false;
        await scheduler.CheckAsync(TestContext.Current.CancellationToken);

        host.Control.GetStatus().OperatingState.ShouldBe("running");
        adapter.Capabilities.ShouldBe([new QueueCapability("ok", "1.0.0")]);
        gate.TryAdmit().ShouldBeTrue();
    }

    [Fact]
    public async Task A_running_Manager_whose_runtime_stops_enters_runtime_unavailable()
    {
        await using var host = Host(Adapter());
        await Restore(host);
        var scheduler = host.Services.GetRequiredService<PreflightScheduler>();
        await scheduler.CheckAsync(TestContext.Current.CancellationToken);
        host.Control.GetStatus().OperatingState.ShouldBe("running");

        _handler.Down = true;
        await scheduler.CheckAsync(TestContext.Current.CancellationToken);

        host.Control.GetStatus().OperatingState.ShouldBe("runtime-unavailable");
        host.Services.GetRequiredService<AdmissionGate>().TryAdmit().ShouldBeFalse();
    }
}
