#pragma warning disable xUnit1051 // The fake handler answers synchronously; there is nothing to cancel.
using System.Net;
using System.Text;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;
using SocAlytics.Analysts.Manager.Platform;
using SocAlytics.Analysts.Manager.Runtime;
using SocAlytics.Analysts.Manager.Runtime.Docker;
using SocAlytics.Analysts.Manager.Tests.Support;

namespace SocAlytics.Analysts.Manager.Tests.Runtime.Docker;

public class DockerAttemptExecutionTests : IDisposable
{
    private const string Image = "registry.test/socalytics/test-analyst@sha256:" + "a1b2c3d4e5f60718293a4b5c6d7e8f90a1b2c3d4e5f60718293a4b5c6d7e8f90";
    private const string Token = "registry-token-secret";
    private const string Secret = "container-output-secret";

    private sealed class FakeEngine : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Path, string? Auth)> Requests { get; } = [];
        public string PullBody { get; set; } = """{"status":"Pulling"}""";
        public string WaitBody { get; set; } = """{"StatusCode":0}""";
        public bool OomKilled { get; set; }
        public bool LoseConnectionOnWait { get; set; }
        public Action? OnStart { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            Requests.Add((request.Method, path, request.Headers.TryGetValues("X-Registry-Auth", out var v) ? v.Single() : null));
            if (path == "/version")
            {
                return Task.FromResult(Json("""{"Version":"28.0.4","ApiVersion":"1.47","Os":"linux","Arch":"amd64"}"""));
            }

            if (path.EndsWith("/images/create", StringComparison.Ordinal))
            {
                return Task.FromResult(Json(PullBody));
            }

            if (path.EndsWith("/containers/create", StringComparison.Ordinal))
            {
                return Task.FromResult(Json("""{"Id":"c1"}""", HttpStatusCode.Created));
            }

            if (path.EndsWith("/start", StringComparison.Ordinal))
            {
                OnStart?.Invoke();
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
            }

            if (path.EndsWith("/wait", StringComparison.Ordinal))
            {
                return LoseConnectionOnWait ? throw new HttpRequestException("connection lost") : Task.FromResult(Json(WaitBody));
            }

            if (path.EndsWith("/c1/json", StringComparison.Ordinal))
            {
                return Task.FromResult(Json("{\"State\":{\"Status\":\"exited\",\"ExitCode\":0,\"OOMKilled\":" + (OomKilled ? "true" : "false") + "}}"));
            }

            if (path.EndsWith("/logs", StringComparison.Ordinal))
            {
                var text = Encoding.UTF8.GetBytes(Secret);
                var frame = new byte[8 + text.Length];
                frame[0] = 1;
                frame[7] = (byte)text.Length;
                text.CopyTo(frame, 8);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(frame) });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
        }

        private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
            new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }

    private readonly FakeTimeProvider _time = new(DateTimeOffset.Parse("2026-10-15T09:00:00Z"));
    private readonly FakeEngine _handler = new();
    private readonly DockerEngineClient _engine;
    private readonly RegistryCredentialProvider _credentials;
    private readonly string _attemptDirectory = Path.Combine(Path.GetTempPath(), "dockerattempt-" + Guid.NewGuid().ToString("N"));

    public DockerAttemptExecutionTests()
    {
        _engine = new DockerEngineClient(new HttpClient(_handler) { BaseAddress = new Uri("http://localhost") });
        var platform = new FakePlatform(_time);
        platform.Script(WorkOperation.RegistryCredentials, () => new HttpResponseMessage(HttpStatusCode.Created)
        {
            Content = new StringContent(
                $$"""{"registry":"registry.test","repositories":["socalytics/test-analyst"],"kind":"bearer","token":"{{Token}}","expiresAt":"2026-10-15T09:05:00Z"}""",
                Encoding.UTF8, "application/json"),
        });
        _credentials = new RegistryCredentialProvider(new PlatformClient(new HttpClient(platform), new ProofFactory(_time)), _time);
        var key = new InMemoryDeviceKeyProvider().ImportFixtureKey();
        _credentials.StepAsync(() => new PlatformSession(new Uri("https://platform.test"), key, "opaque"), CancellationToken.None).GetAwaiter().GetResult();
        Directory.CreateDirectory(Path.Combine(_attemptDirectory, "result"));
    }

    public void Dispose()
    {
        _engine.Dispose();
        _credentials.Dispose();
        if (Directory.Exists(_attemptDirectory))
        {
            Directory.Delete(_attemptDirectory, true);
        }
    }

    private AttemptLaunch Launch() => new(
        "attempt-1", "job-1", "cap", "cap:1", "scope", "sha-256:p", "sha-256:i", string.Empty,
        new AttemptResources(1, 256L * 1024 * 1024, []),
        ResultDirectory: Path.Combine(_attemptDirectory, "result"), JobFile: Path.Combine(_attemptDirectory, "job.json"),
        ScratchDirectory: Path.Combine(_attemptDirectory, "scratch"));

    private DockerAttemptExecution Start(string image = Image) =>
        new(_engine, _credentials, Launch(), image, "manager-1", DockerLaunchSpec.FixedUser);

    private void WriteManifest() => File.WriteAllText(Path.Combine(_attemptDirectory, "result", "manifest.json"), "{}");

    private bool Called(string suffix) => _handler.Requests.Any(r => r.Path.EndsWith(suffix, StringComparison.Ordinal));

    [Fact]
    public async Task Succeeds_with_exit_zero_and_a_manifest_and_keeps_the_log_on_the_host()
    {
        WriteManifest();

        var outcome = await Start().Outcome;

        outcome.Succeeded.ShouldBeTrue();
        outcome.ResultReference.ShouldBe(Path.Combine(_attemptDirectory, "result", "manifest.json"));
        outcome.ResultChecksum!.ShouldStartWith("sha-256:");
        _handler.Requests.Single(r => r.Path.EndsWith("/images/create", StringComparison.Ordinal)).Auth.ShouldNotBeNull();
        (await File.ReadAllTextAsync(Path.Combine(_attemptDirectory, "container.log"))).ShouldBe(Secret);
    }

    [Fact]
    public async Task Non_zero_exit_fails_as_analyst_error_without_log_content()
    {
        _handler.WaitBody = """{"StatusCode":3}""";

        var outcome = await Start().Outcome;

        outcome.Succeeded.ShouldBeFalse();
        outcome.FailureReason.ShouldBe("analyst-error");
        outcome.ToString().ShouldNotContain(Secret);
    }

    [Fact]
    public async Task Exit_zero_without_a_manifest_fails_as_analyst_error()
    {
        var outcome = await Start().Outcome;

        outcome.FailureReason.ShouldBe("analyst-error");
    }

    [Fact]
    public async Task Out_of_memory_fails_as_resource_exhausted()
    {
        _handler.WaitBody = """{"StatusCode":137}""";
        _handler.OomKilled = true;

        (await Start().Outcome).FailureReason.ShouldBe("resource-exhausted");
    }

    [Fact]
    public async Task Pull_failure_fails_as_image_unavailable_before_creating_a_container()
    {
        _handler.PullBody = """{"error":"unauthorized: denied"}""";

        (await Start().Outcome).FailureReason.ShouldBe("image-unavailable");
        Called("/containers/create").ShouldBeFalse();
    }

    [Theory]
    [InlineData("other.test/socalytics/test-analyst@sha256:abc")]
    [InlineData("registry.test/other/repo@sha256:abc")]
    public async Task Reference_outside_the_prefix_fails_before_any_pull(string image)
    {
        (await Start(image).Outcome).FailureReason.ShouldBe("image-unavailable");
        Called("/images/create").ShouldBeFalse();
    }

    [Fact]
    public async Task Lost_connection_while_waiting_fails_as_runtime_unavailable()
    {
        _handler.LoseConnectionOnWait = true;

        (await Start().Outcome).FailureReason.ShouldBe("runtime-unavailable");
    }

    [Fact]
    public async Task Stop_and_clean_up_stop_and_remove_the_container()
    {
        var execution = Start();
        WriteManifest();
        await execution.Outcome;

        await execution.StopAsync(CancellationToken.None);
        await execution.CleanUpAsync(CancellationToken.None);

        _handler.Requests.ShouldContain(r => r.Method == HttpMethod.Post && r.Path.EndsWith("/containers/c1/stop", StringComparison.Ordinal));
        _handler.Requests.ShouldContain(r => r.Method == HttpMethod.Delete && r.Path.EndsWith("/containers/c1", StringComparison.Ordinal));
    }
}
