#pragma warning disable xUnit1051 // The fake handler answers synchronously; there is nothing to cancel.
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Shouldly;
using Xunit;
using SocAlytics.Analysts.Manager.Runtime.Docker;

namespace SocAlytics.Analysts.Manager.Tests.Runtime.Docker;

public class DockerEngineClientTests : IDisposable
{
    private sealed record Recorded(HttpMethod Method, string PathAndQuery, string? Body, HttpRequestMessage Request);

    private sealed class FakeHandler : HttpMessageHandler
    {
        public List<Recorded> Requests { get; } = [];
        public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } = _ => new HttpResponseMessage(HttpStatusCode.OK);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new Recorded(request.Method, request.RequestUri!.PathAndQuery, body, request));
            return Respond(request);
        }
    }

    private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private const string VersionJson = """{"Platform":{"Name":"Docker Desktop 4.40.0"},"Version":"28.0.4","ApiVersion":"1.48","MinAPIVersion":"1.24","Os":"linux","Arch":"amd64"}""";

    private readonly FakeHandler _handler = new();
    private readonly FakeLogCollector _logs = new();
    private readonly DockerEngineClient _client;

    public DockerEngineClientTests()
    {
        _handler.Respond = request => request.RequestUri!.AbsolutePath switch
        {
            "/version" => Json(VersionJson),
            _ => new HttpResponseMessage(HttpStatusCode.OK),
        };
        var logger = new FakeLogger(_logs);
        _client = new DockerEngineClient(new HttpClient(_handler) { BaseAddress = new Uri("http://localhost") }, logger);
    }

    public void Dispose() => _client.Dispose();

    private Func<HttpRequestMessage, HttpResponseMessage> WithVersion(Func<HttpRequestMessage, HttpResponseMessage> next) =>
        request => request.RequestUri!.AbsolutePath == "/version" ? Json(VersionJson) : next(request);

    [Fact]
    public async Task Ping_uses_the_unversioned_path()
    {
        (await _client.PingAsync(CancellationToken.None)).ShouldBeTrue();

        _handler.Requests.Single().PathAndQuery.ShouldBe("/_ping");
    }

    [Fact]
    public async Task Version_is_negotiated_once_capped_and_pinned_into_paths()
    {
        _handler.Respond = WithVersion(_ => Json("""{"OSType":"linux","OperatingSystem":"Docker Desktop","NCPU":8,"MemTotal":17179869184}"""));

        var first = await _client.GetInfoAsync(CancellationToken.None);
        await _client.GetInfoAsync(CancellationToken.None);

        first.OsType.ShouldBe("linux");
        first.Ncpu.ShouldBe(8);
        first.MemTotal.ShouldBe(17179869184);
        _client.ApiVersion.ShouldBe("1.47");
        _handler.Requests.Select(r => r.PathAndQuery).ShouldBe(["/version", "/v1.47/info", "/v1.47/info"]);
    }

    [Fact]
    public async Task Version_below_the_minimum_is_unsupported()
    {
        _handler.Respond = _ => Json("""{"ApiVersion":"1.42","Version":"23.0.0"}""");

        var ex = await Should.ThrowAsync<DockerEngineException>(() => _client.GetInfoAsync(CancellationToken.None));

        ex.Kind.ShouldBe(DockerErrorKind.UnsupportedVersion);
        _handler.Requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Version_maps_the_platform_name()
    {
        _handler.Respond = _ => Json(VersionJson);

        var version = await _client.GetVersionAsync(CancellationToken.None);

        version.PlatformName.ShouldBe("Docker Desktop 4.40.0");
        version.Version.ShouldBe("28.0.4");
        version.Os.ShouldBe("linux");
        version.Arch.ShouldBe("amd64");
    }

    [Fact]
    public async Task Pull_sends_the_registry_token_only_in_the_header_and_never_logs_it()
    {
        const string token = "registry-secret-token";
        _handler.Respond = WithVersion(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"status\":\"Pulling\"}\n{\"status\":\"Digest: sha256:abc\"}\n"),
        });

        await _client.PullImageAsync("registry.test/analysts/a@sha256:" + new string('a', 64), token, CancellationToken.None);

        var pull = _handler.Requests.Last();
        pull.Method.ShouldBe(HttpMethod.Post);
        pull.PathAndQuery.ShouldStartWith("/v1.47/images/create?fromImage=registry.test%2Fanalysts%2Fa%40sha256%3A");
        var header = pull.Request.Headers.GetValues("X-Registry-Auth").Single();
        var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(header.Replace('-', '+').Replace('_', '/')));
        JsonNode.Parse(decoded)!["registrytoken"]!.GetValue<string>().ShouldBe(token);
        pull.PathAndQuery.ShouldNotContain(token);
        _logs.GetSnapshot().ShouldAllBe(r => !r.Message.Contains(token) && !r.Message.Contains(header));
    }

    [Fact]
    public async Task Pull_without_a_token_sends_no_registry_header()
    {
        _handler.Respond = WithVersion(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"status\":\"ok\"}\n") });

        await _client.PullImageAsync("registry.test/a@sha256:" + new string('b', 64), null, CancellationToken.None);

        _handler.Requests.Last().Request.Headers.Contains("X-Registry-Auth").ShouldBeFalse();
    }

    [Theory]
    [InlineData("unauthorized: authentication required", DockerErrorKind.Unauthorized)]
    [InlineData("manifest unknown", DockerErrorKind.NotFound)]
    [InlineData("boom", DockerErrorKind.Failed)]
    public async Task Pull_stream_errors_are_mapped(string error, DockerErrorKind kind)
    {
        _handler.Respond = WithVersion(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent($"{{\"status\":\"Pulling\"}}\n{{\"error\":\"{error}\"}}\n"),
        });

        var ex = await Should.ThrowAsync<DockerEngineException>(
            () => _client.PullImageAsync("registry.test/a@sha256:" + new string('c', 64), "t", CancellationToken.None));

        ex.Kind.ShouldBe(kind);
    }

    [Fact]
    public async Task Image_inspect_and_remove_use_the_digest_path_and_map_not_found()
    {
        var digest = "registry.test/a@sha256:" + new string('d', 64);
        _handler.Respond = WithVersion(request => request.Method == HttpMethod.Get
            ? Json("""{"Id":"sha256:1","Os":"linux","Architecture":"amd64","Size":42}""")
            : new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("""{"message":"No such image"}""") });

        var image = await _client.InspectImageAsync(digest, CancellationToken.None);
        var removed = await _client.RemoveImageAsync(digest, CancellationToken.None);

        image!.Architecture.ShouldBe("amd64");
        image.Size.ShouldBe(42);
        removed.ShouldBeFalse();
        _handler.Requests[1].PathAndQuery.ShouldBe($"/v1.47/images/{digest}/json");
        _handler.Requests[2].Method.ShouldBe(HttpMethod.Delete);
        _handler.Requests[2].PathAndQuery.ShouldBe($"/v1.47/images/{digest}");
    }

    [Fact]
    public async Task Container_lifecycle_uses_the_expected_paths_and_bodies()
    {
        _handler.Respond = WithVersion(request => request.RequestUri!.AbsolutePath switch
        {
            "/v1.47/containers/create" => Json("""{"Id":"abc123"}""", HttpStatusCode.Created),
            "/v1.47/containers/abc123/wait" => Json("""{"StatusCode":3}"""),
            "/v1.47/containers/abc123/json" => Json("""{"State":{"Status":"exited","Running":false,"ExitCode":3,"OOMKilled":true,"StartedAt":"2026-10-15T10:00:00Z","FinishedAt":"0001-01-01T00:00:00Z"}}"""),
            _ => new HttpResponseMessage(HttpStatusCode.NoContent),
        });

        var id = await _client.CreateContainerAsync(new JsonObject { ["Image"] = "x@sha256:1" }, "socalytics-attempt-1", CancellationToken.None);
        await _client.StartContainerAsync(id, CancellationToken.None);
        var wait = await _client.WaitContainerAsync(id, CancellationToken.None);
        var state = await _client.InspectContainerAsync(id, CancellationToken.None);
        await _client.StopContainerAsync(id, TimeSpan.FromSeconds(10), CancellationToken.None);
        await _client.RemoveContainerAsync(id, true, CancellationToken.None);

        id.ShouldBe("abc123");
        wait.StatusCode.ShouldBe(3);
        state!.ExitCode.ShouldBe(3);
        state.OomKilled.ShouldBeTrue();
        state.StartedAt.ShouldNotBeNull();
        state.FinishedAt.ShouldBeNull();
        var calls = _handler.Requests.Skip(1).Select(r => $"{r.Method} {r.PathAndQuery}").ToArray();
        calls.ShouldBe(
        [
            "POST /v1.47/containers/create?name=socalytics-attempt-1",
            "POST /v1.47/containers/abc123/start",
            "POST /v1.47/containers/abc123/wait?condition=not-running",
            "GET /v1.47/containers/abc123/json",
            "POST /v1.47/containers/abc123/stop?t=10",
            "DELETE /v1.47/containers/abc123?force=true&v=true",
        ]);
        JsonNode.Parse(_handler.Requests[1].Body!)!["Image"]!.GetValue<string>().ShouldBe("x@sha256:1");
    }

    [Fact]
    public async Task Start_and_stop_tolerate_already_in_state()
    {
        _handler.Respond = WithVersion(_ => new HttpResponseMessage(HttpStatusCode.NotModified));

        await Should.NotThrowAsync(() => _client.StartContainerAsync("abc", CancellationToken.None));
        await Should.NotThrowAsync(() => _client.StopContainerAsync("abc", TimeSpan.Zero, CancellationToken.None));
    }

    [Fact]
    public async Task Logs_are_demultiplexed()
    {
        var payload = new List<byte>();
        void Frame(byte stream, string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            payload.AddRange([stream, 0, 0, 0, 0, 0, (byte)(bytes.Length >> 8), (byte)bytes.Length]);
            payload.AddRange(bytes);
        }

        Frame(1, "out\n");
        Frame(2, "err\n");
        _handler.Respond = WithVersion(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload.ToArray()) });

        var logs = await _client.GetLogsAsync("abc", 50, CancellationToken.None);

        logs.Stdout.ShouldBe("out\n");
        logs.Stderr.ShouldBe("err\n");
        _handler.Requests.Last().PathAndQuery.ShouldBe("/v1.47/containers/abc/logs?stdout=1&stderr=1&tail=50");
    }

    [Fact]
    public async Task Container_list_filters_by_label()
    {
        _handler.Respond = WithVersion(_ => Json("""[{"Id":"c1","State":"running","Labels":{"socalytics.attempt":"a1"}}]"""));

        var list = await _client.ListContainersAsync(["socalytics.attempt"], CancellationToken.None);

        list.Single().Labels["socalytics.attempt"].ShouldBe("a1");
        var path = Uri.UnescapeDataString(_handler.Requests.Last().PathAndQuery);
        path.ShouldBe("/v1.47/containers/json?all=true&filters={\"label\":[\"socalytics.attempt\"]}");
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, DockerErrorKind.Unauthorized)]
    [InlineData(HttpStatusCode.NotFound, DockerErrorKind.NotFound)]
    [InlineData(HttpStatusCode.Conflict, DockerErrorKind.Conflict)]
    [InlineData(HttpStatusCode.BadRequest, DockerErrorKind.BadRequest)]
    [InlineData(HttpStatusCode.InternalServerError, DockerErrorKind.ServerError)]
    public async Task Status_codes_map_to_error_kinds(HttpStatusCode status, DockerErrorKind kind)
    {
        _handler.Respond = WithVersion(_ => Json("""{"message":"nope"}""", status));

        var ex = await Should.ThrowAsync<DockerEngineException>(() => _client.StartContainerAsync("abc", CancellationToken.None));

        ex.Kind.ShouldBe(kind);
    }

    [Fact]
    public async Task Connection_failures_are_reported_as_unreachable()
    {
        _handler.Respond = _ => throw new HttpRequestException("connection refused");

        var ex = await Should.ThrowAsync<DockerEngineException>(() => _client.PingAsync(CancellationToken.None));

        ex.Kind.ShouldBe(DockerErrorKind.Unreachable);
    }

    [Fact]
    public async Task Unsafe_names_are_rejected_before_a_request()
    {
        await Should.ThrowAsync<ArgumentException>(() => _client.InspectContainerAsync("../etc?x=1", CancellationToken.None));

        _handler.Requests.ShouldBeEmpty();
    }
}
