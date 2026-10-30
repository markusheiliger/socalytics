using System.Net;
using System.Text;
using Shouldly;
using SocAlytics.Platform.EndToEnd.Tests.Support;
using Xunit;

namespace SocAlytics.Platform.EndToEnd.Tests.ImagePush;

/// <summary>The registry manifest lookup and the engine image-record lookup, against a fake endpoint and without Docker.</summary>
public sealed class DockerEngineLookupTests
{
    private const string Digest = "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string OtherDigest = "sha256:fedcba9876543210fedcba9876543210fedcba9876543210fedcba9876543210";
    private const string Repository = "analysts/probe";
    private const string Tag = "1.0.0";
    private const string EngineName = "localhost:5000/analysts/probe";

    private static readonly Uri Registry = new("http://localhost:5000");

    [Fact]
    public async Task Registry_lookup_reads_the_content_digest_header_with_all_manifest_accept_types()
    {
        var handler = new FakeHandler(_ => ManifestResponse(Digest));
        using var engine = new DockerEngine(handler, Registry);

        var result = await engine.LookupRegistryDigestAsync(Repository, Tag, CancellationToken.None);

        result.Digest.ShouldBe(Digest);
        result.Outcome.ShouldBe("found");
        var request = handler.Requests.ShouldHaveSingleItem();
        request.Method.ShouldBe(HttpMethod.Get);
        request.RequestUri!.AbsoluteUri.ShouldBe($"http://localhost:5000/v2/{Repository}/manifests/{Tag}");
        request.Accept.ShouldBe(
        [
            "application/vnd.oci.image.index.v1+json",
            "application/vnd.oci.image.manifest.v1+json",
            "application/vnd.docker.distribution.manifest.list.v2+json",
            "application/vnd.docker.distribution.manifest.v2+json",
        ],
        ignoreOrder: true);
    }

    [Fact]
    public async Task Registry_lookup_reports_absent_for_a_missing_manifest_or_header()
    {
        using var missing = new DockerEngine(new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)), Registry);
        using var headerless = new DockerEngine(new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)), Registry);

        (await missing.LookupRegistryDigestAsync(Repository, Tag, CancellationToken.None)).Outcome.ShouldBe("absent");
        (await headerless.LookupRegistryDigestAsync(Repository, Tag, CancellationToken.None)).Outcome.ShouldBe("absent");
    }

    [Fact]
    public async Task Registry_lookup_never_accepts_a_malformed_digest()
    {
        using var engine = new DockerEngine(new FakeHandler(_ => ManifestResponse("sha256:ABC")), Registry);

        var result = await engine.LookupRegistryDigestAsync(Repository, Tag, CancellationToken.None);

        result.Digest.ShouldBeNull();
        result.Outcome.ShouldStartWith("unavailable");
    }

    [Fact]
    public async Task Registry_lookup_reports_a_server_error_as_unavailable()
    {
        using var failing = new DockerEngine(new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)), Registry);

        (await failing.LookupRegistryDigestAsync(Repository, Tag, CancellationToken.None)).Outcome.ShouldBe("unavailable: registry answered 500");
    }

    [Fact]
    public async Task Registry_lookup_repeats_a_refused_connection_and_succeeds_when_the_registry_answers()
    {
        var calls = 0;
        using var engine = new DockerEngine(new FakeHandler(_ => ++calls < 2
            ? throw new HttpRequestException("connection refused")
            : ManifestResponse(Digest)), Registry);

        var result = await engine.LookupRegistryDigestAsync(Repository, Tag, CancellationToken.None);

        result.Digest.ShouldBe(Digest);
        calls.ShouldBe(2);
    }

    [Fact]
    public async Task Registry_lookup_without_a_registry_address_is_unavailable_and_sends_nothing()
    {
        var handler = new FakeHandler(_ => ManifestResponse(Digest));
        using var engine = new DockerEngine(handler, null);

        var result = await engine.LookupRegistryDigestAsync(Repository, Tag, CancellationToken.None);

        result.Outcome.ShouldStartWith("unavailable");
        handler.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task Registry_lookup_propagates_the_callers_cancellation()
    {
        using var engine = new DockerEngine(new FakeHandler(_ => ManifestResponse(Digest)), Registry);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => engine.LookupRegistryDigestAsync(Repository, Tag, cts.Token));
    }

    [Fact]
    public async Task Engine_lookup_reads_the_repo_digest_of_the_pushed_repository_only()
    {
        var record = $$"""
            {"Id":"sha256:1","RepoDigests":["docker.io/library/other@{{OtherDigest}}","{{EngineName}}@{{Digest}}"]}
            """;
        var handler = new FakeHandler(_ => JsonResponse(record));
        using var engine = new DockerEngine(handler, Registry);

        var result = await engine.LookupEngineDigestAsync(EngineName, Tag, CancellationToken.None);

        result.Digest.ShouldBe(Digest);
        result.Outcome.ShouldBe("found");
        var request = handler.Requests.ShouldHaveSingleItem();
        request.Method.ShouldBe(HttpMethod.Get);
        Uri.UnescapeDataString(request.RequestUri!.AbsolutePath).ShouldBe($"/images/{EngineName}:{Tag}/json");
    }

    [Fact]
    public async Task Engine_lookup_is_absent_without_an_entry_for_the_pushed_repository()
    {
        var record = $$"""{"RepoDigests":["docker.io/library/other@{{OtherDigest}}"]}""";
        using var other = new DockerEngine(new FakeHandler(_ => JsonResponse(record)), Registry);
        using var none = new DockerEngine(new FakeHandler(_ => JsonResponse("""{"RepoDigests":null}""")), Registry);
        using var missing = new DockerEngine(new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)), Registry);

        (await other.LookupEngineDigestAsync(EngineName, Tag, CancellationToken.None)).Outcome.ShouldBe("absent");
        (await none.LookupEngineDigestAsync(EngineName, Tag, CancellationToken.None)).Outcome.ShouldBe("absent");
        (await missing.LookupEngineDigestAsync(EngineName, Tag, CancellationToken.None)).Outcome.ShouldBe("absent");
    }

    [Fact]
    public async Task Engine_lookup_never_accepts_a_malformed_digest()
    {
        var record = $$"""{"RepoDigests":["{{EngineName}}@sha256:short"]}""";
        using var engine = new DockerEngine(new FakeHandler(_ => JsonResponse(record)), Registry);

        var result = await engine.LookupEngineDigestAsync(EngineName, Tag, CancellationToken.None);

        result.Digest.ShouldBeNull();
        result.Outcome.ShouldStartWith("unavailable");
    }

    [Fact]
    public async Task An_engine_without_the_image_record_lookup_does_not_hide_the_earlier_diagnostics()
    {
        using var engine = new DockerEngine(new FakeHandler(request => request.RequestUri!.Host == "localhost"
            ? new HttpResponseMessage(HttpStatusCode.NotFound)
            : new HttpResponseMessage(HttpStatusCode.NotImplemented)), Registry);

        var registry = await engine.LookupRegistryDigestAsync(Repository, Tag, CancellationToken.None);
        var record = await engine.LookupEngineDigestAsync(EngineName, Tag, CancellationToken.None);

        registry.Outcome.ShouldBe("absent");
        record.Outcome.ShouldBe("unavailable: engine answered 501");
        record.Digest.ShouldBeNull();
    }

    [Fact]
    public async Task Registry_and_engine_that_were_both_consulted_can_report_different_digests()
    {
        var record = $$"""{"RepoDigests":["{{EngineName}}@{{OtherDigest}}"]}""";
        using var engine = new DockerEngine(new FakeHandler(request => request.RequestUri!.Host == "localhost"
            ? ManifestResponse(Digest)
            : JsonResponse(record)), Registry);

        var registry = await engine.LookupRegistryDigestAsync(Repository, Tag, CancellationToken.None);
        var image = await engine.LookupEngineDigestAsync(EngineName, Tag, CancellationToken.None);

        registry.Digest.ShouldBe(Digest);
        image.Digest.ShouldBe(OtherDigest);
    }

    private static HttpResponseMessage ManifestResponse(string digest)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK);
        response.Headers.TryAddWithoutValidation("Docker-Content-Digest", digest);
        return response;
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    private sealed record RecordedRequest(HttpMethod Method, Uri? RequestUri, IReadOnlyList<string> Accept);

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<RecordedRequest> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(new RecordedRequest(
                request.Method,
                request.RequestUri,
                request.Headers.Accept.Select(a => a.MediaType!).ToList()));
            return Task.FromResult(respond(request));
        }
    }
}
