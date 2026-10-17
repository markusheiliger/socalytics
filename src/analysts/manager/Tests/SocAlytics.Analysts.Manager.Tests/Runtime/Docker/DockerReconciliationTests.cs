#pragma warning disable xUnit1051 // The fake handler answers synchronously; there is nothing to cancel.
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;
using SocAlytics.Analysts.Manager.Platform;
using SocAlytics.Analysts.Manager.Runtime;
using SocAlytics.Analysts.Manager.Runtime.Docker;
using SocAlytics.Analysts.Manager.Work;

namespace SocAlytics.Analysts.Manager.Tests.Runtime.Docker;

public sealed class DockerReconciliationTests : IDisposable
{
    private const string ManagerId = "manager-1";
    private static readonly Guid ActiveId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    private sealed class FakeEngine : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Path, string Query)> Requests { get; } = [];
        public List<(string Id, string Attempt)> Containers { get; } = [];
        public HashSet<string> RefuseRemoval { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            Requests.Add((request.Method, path, Uri.UnescapeDataString(request.RequestUri.Query)));
            if (path == "/version")
            {
                return Task.FromResult(Json("""{"Version":"28.0.4","ApiVersion":"1.47","Os":"linux","Arch":"amd64"}"""));
            }

            if (path.EndsWith("/containers/json", StringComparison.Ordinal))
            {
                var array = new JsonArray();
                foreach (var (id, attempt) in Containers)
                {
                    array.Add(new JsonObject
                    {
                        ["Id"] = id,
                        ["State"] = "running",
                        ["Labels"] = new JsonObject { [DockerLaunchSpec.ManagerLabel] = ManagerId, [DockerLaunchSpec.AttemptLabel] = attempt },
                    });
                }

                return Task.FromResult(Json(array.ToJsonString()));
            }

            if (request.Method == HttpMethod.Delete)
            {
                var id = path[(path.LastIndexOf('/') + 1)..];
                if (RefuseRemoval.Contains(id))
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)
                    {
                        Content = new StringContent("""{"message":"busy"}""", Encoding.UTF8, "application/json"),
                    });
                }

                Containers.RemoveAll(c => c.Id == id);
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
        }

        private static HttpResponseMessage Json(string json) =>
            new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }

    private readonly FakeTimeProvider _time = new(DateTimeOffset.Parse("2026-10-17T09:00:00Z"));
    private readonly FakeEngine _handler = new();
    private readonly DockerEngineClient _engine;
    private readonly AttemptRegistry _attempts = new();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "reconcile-test-" + Guid.NewGuid().ToString("N"));
    private readonly DockerReconciliationService _service;

    public DockerReconciliationTests()
    {
        _engine = new DockerEngineClient(new HttpClient(_handler) { BaseAddress = new Uri("http://localhost") });
        Directory.CreateDirectory(_root);
        _service = new DockerReconciliationService(() => _engine, () => ManagerId, _attempts, _time, attemptsRoot: _root);
    }

    public void Dispose()
    {
        _engine.Dispose();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }

    private static ActiveAttempt Attempt(Guid id) => new(
        new AnalystJobMessage(
            Guid.NewGuid().ToString(), Guid.NewGuid().ToString(), "run-1", "node-1", Guid.NewGuid().ToString(), "team-1", "match-1",
            new QueueCapability("match-summary", "1.0.0"), "1.0.0", "match", [],
            new JobPinned("sha-256:" + new string('3', 64), "sha-256:" + new string('4', 64), null, "profile-1", []),
            new JobResources(500, 1024), new CompletionCorrelation("corr-1"), "rsv-1"),
        new ClaimGranted(id, Guid.NewGuid(), Guid.NewGuid(), 1, 7, DateTimeOffset.UtcNow.AddSeconds(60), 60, 120, 600, false),
        DateTimeOffset.UtcNow);

    private async Task<TaskCompletionSource> RunActiveAttemptAsync()
    {
        var release = new TaskCompletionSource();
        var started = new TaskCompletionSource();
        _ = _attempts.Run(async (register, token) =>
        {
            register(Attempt(ActiveId));
            started.SetResult();
            await release.Task.WaitAsync(token);
        }, CancellationToken.None);
        await started.Task;
        return release;
    }

    [Fact]
    public async Task Lists_only_with_the_manager_label_filter_and_removes_leftovers_of_a_previous_process()
    {
        _handler.Containers.Add(("old-1", Guid.NewGuid().ToString()));
        var oldDirectory = Directory.CreateDirectory(Path.Combine(_root, Guid.NewGuid().ToString("N"))).FullName;

        var state = await _service.ReconcileAsync(CancellationToken.None);

        state!.RemovedContainers.ShouldBe(1);
        state.RemovedDirectories.ShouldBe(1);
        state.Leftovers.ShouldBeEmpty();
        Directory.Exists(oldDirectory).ShouldBeFalse();
        var list = _handler.Requests.Where(r => r.Path.EndsWith("/containers/json", StringComparison.Ordinal)).ToList();
        list.ShouldNotBeEmpty();
        list.ShouldAllBe(r => r.Query.Contains($"{DockerLaunchSpec.ManagerLabel}={ManagerId}"));
        _handler.Requests.ShouldContain(r => r.Method == HttpMethod.Delete && r.Path.EndsWith("/containers/old-1", StringComparison.Ordinal));
        _handler.Requests.ShouldNotContain(r => r.Path.Contains("/heartbeat") || r.Path.Contains("/completion"));
    }

    [Fact]
    public async Task Keeps_the_container_and_directory_of_an_active_attempt()
    {
        var release = await RunActiveAttemptAsync();
        _handler.Containers.Add(("active-1", ActiveId.ToString()));
        _handler.Containers.Add(("old-1", Guid.NewGuid().ToString()));
        var activeDirectory = Directory.CreateDirectory(Path.Combine(_root, ActiveId.ToString("N"))).FullName;

        var state = await _service.ReconcileAsync(CancellationToken.None);

        state!.RemovedContainers.ShouldBe(1);
        state.RemovedDirectories.ShouldBe(0);
        _handler.Containers.Select(c => c.Id).ShouldBe(["active-1"]);
        Directory.Exists(activeDirectory).ShouldBeTrue();
        release.SetResult();
    }

    [Fact]
    public async Task Revocation_removes_every_container_and_directory_including_active_ones()
    {
        var release = await RunActiveAttemptAsync();
        _handler.Containers.Add(("active-1", ActiveId.ToString()));
        var activeDirectory = Directory.CreateDirectory(Path.Combine(_root, ActiveId.ToString("N"))).FullName;

        var state = await _service.ReconcileAsync(CancellationToken.None, removeAll: true);

        state!.RemovedContainers.ShouldBe(1);
        _handler.Containers.ShouldBeEmpty();
        Directory.Exists(activeDirectory).ShouldBeFalse();
        release.SetResult();
    }

    [Fact]
    public async Task A_failed_removal_is_shown_and_retried_by_the_next_run()
    {
        _handler.Containers.Add(("stuck-1", Guid.NewGuid().ToString()));
        _handler.RefuseRemoval.Add("stuck-1");

        var first = await _service.ReconcileAsync(CancellationToken.None);

        first!.RemovedContainers.ShouldBe(0);
        first.Leftovers.ShouldHaveSingleItem().ShouldContain("stuck-1");
        _service.State.ShouldBe(first);

        _handler.RefuseRemoval.Clear();
        var second = await _service.ReconcileAsync(CancellationToken.None);

        second!.RemovedContainers.ShouldBe(1);
        second.Leftovers.ShouldBeEmpty();
        _handler.Containers.ShouldBeEmpty();
    }

    [Fact]
    public async Task Without_a_registration_id_nothing_is_listed()
    {
        var service = new DockerReconciliationService(() => _engine, () => null, _attempts, _time, attemptsRoot: _root);

        (await service.ReconcileAsync(CancellationToken.None)).ShouldBeNull();

        _handler.Requests.ShouldBeEmpty();
    }

    [Fact]
    public void Status_text_shows_the_reconciliation_and_what_could_not_be_removed()
    {
        var result = new PreflightResult(_time.GetUtcNow(), "docker-desktop", "Docker Desktop", "4.40", "linux", true, [], [])
        {
            Reconciliation = new ReconciliationState(_time.GetUtcNow(), 2, 1, ["container stuck-1"]),
        };

        var text = string.Join('\n', SocAlytics.Analysts.Manager.Diagnostics.RuntimeStatusText.Lines(result));

        text.ShouldContain("removed 2 containers and 1 attempt directories");
        text.ShouldContain("Could not remove: container stuck-1");
    }
}
