using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Shouldly;
using SocAlytics.Platform.Images.Tests.Support;
using Xunit;

namespace SocAlytics.Platform.Images.Tests;

[Collection(ImageBuildCollection.Name)]
public sealed partial class WebUiImageTests(StampDependenciesFixture stamp) : IClassFixture<StampDependenciesFixture>
{
    private const string ApiDockerfile = "src/platform/SocAlytics.Platform.Api/Dockerfile";
    private const string WebDockerfile = "src/clients/web/Dockerfile";
    private const string ApiAlias = "api";
    private const int Port = 8080;
    private const string StorageOrigin = "https://storage.example.test:9000";
    private const string MinimumVersionHeader = "SocAlytics-Minimum-Web-Version";

    // contracts/container-images.md section 6.3; the text of build/securityHeaders.ts.
    private const string PolicyPrefix =
        "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; font-src 'self'; media-src blob:; connect-src 'self'";

    private const string PolicySuffix =
        "; worker-src 'self'; manifest-src 'self'; object-src 'none'; base-uri 'none'; frame-ancestors 'none'; form-action 'self'; require-trusted-types-for 'script'; trusted-types default react-dom upload-worker service-worker";

    private static readonly TimeSpan ReadyWithin = TimeSpan.FromSeconds(60);

    private ContainerProbe Probe => stamp.Probe;

    [Fact]
    public async Task Web_ui_serves_the_built_shell_byte_for_byte_with_the_contract_headers()
    {
        var ct = TestContext.Current.CancellationToken;
        var address = await StartWebUiAsync($"http://{ApiAlias}:{Port}", StorageOrigin, null, ct);
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

        var manifestBytes = await http.GetByteArrayAsync(address + "/shell-manifest.json", ct);
        var manifest = JsonDocument.Parse(manifestBytes).RootElement;
        manifest.GetProperty("version").GetString().ShouldBe(ImageBuildFixture.TestVersion);
        var files = manifest.GetProperty("files").EnumerateArray()
            .Select(file => (Url: file.GetProperty("url").GetString()!, Integrity: file.GetProperty("integrity").GetString()!))
            .ToList();
        files.ShouldNotBeEmpty();

        foreach (var (url, integrity) in files)
        {
            var body = await http.GetByteArrayAsync(address + url, ct);
            Integrity(body).ShouldBe(integrity, $"the served {url} must match its shell manifest integrity");
        }

        var index = await http.GetStringAsync(address + "/index.html", ct);
        var tags = IntegrityTagRegex().Matches(index).Select(match => match.Value).ToList();
        tags.ShouldNotBeEmpty("index.html references integrity-protected scripts and stylesheets");
        foreach (var tag in tags)
        {
            var reference = AttributeRegex().Matches(tag).ToDictionary(m => m.Groups[1].Value.ToLowerInvariant(), m => m.Groups[2].Value);
            var path = reference.TryGetValue("src", out var src) ? src : reference["href"];
            reference.TryGetValue("integrity", out var integrity).ShouldBeTrue(tag);
            Integrity(await http.GetByteArrayAsync(address + path, ct)).ShouldBe(integrity, tag);
        }

        (await http.GetStringAsync(address + "/sw.js", ct)).ShouldContain(ImageBuildFixture.TestVersion);

        foreach (var path in new[] { "/index.html", "/sw.js", "/shell-manifest.json", "/manifest.webmanifest", "/icons/icon-192.png" })
        {
            using var response = await http.GetAsync(address + path, ct);
            response.StatusCode.ShouldBe(HttpStatusCode.OK, path);
            Header(response, "Cache-Control").ShouldBe("no-cache", path);
        }

        var asset = files.Select(file => file.Url).First(url => url.StartsWith("/assets/", StringComparison.Ordinal));
        using (var response = await http.GetAsync(address + asset, ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            Header(response, "Cache-Control").ShouldBe("public, max-age=31536000, immutable");
        }

        using (var response = await http.GetAsync(address + "/assets/missing-0123456789.js", ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
            Header(response, "Cache-Control").ShouldBe("no-cache");
        }

        using (var response = await http.GetAsync(address + "/matches/42/recordings", ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            (await response.Content.ReadAsStringAsync(ct)).ShouldBe(index);
            Header(response, "Cache-Control").ShouldBe("no-cache");
        }

        using (var response = await http.GetAsync(address + "/manifest.webmanifest", ct))
        {
            response.Content.Headers.ContentType?.MediaType.ShouldBe("application/manifest+json");
        }

        foreach (var path in new[] { "/index.html", "/assets/missing-0123456789.js", "/matches/42" })
        {
            using var response = await http.GetAsync(address + path, ct);
            AssertSecurityHeaders(response, $"{PolicyPrefix} {StorageOrigin}{PolicySuffix}");
        }
    }

    [Fact]
    public async Task Web_ui_without_an_object_storage_origin_sends_the_policy_without_it()
    {
        var ct = TestContext.Current.CancellationToken;
        var address = await StartWebUiAsync($"http://{ApiAlias}:{Port}", null, null, ct);
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

        foreach (var path in new[] { "/index.html", "/assets/missing-0123456789.js", "/matches/42" })
        {
            using var response = await http.GetAsync(address + path, ct);
            AssertSecurityHeaders(response, PolicyPrefix + PolicySuffix);
        }
    }

    [Fact]
    public async Task Web_ui_forwards_alive_and_api_requests_to_the_api_container()
    {
        var ct = TestContext.Current.CancellationToken;
        var api = await StartApiAsync(ct);
        var address = await StartWebUiAsync($"http://{ApiAlias}:{Port}", null, null, ct);
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

        (await WaitForStatusAsync(http, address + "/alive", HttpStatusCode.OK, api, ct)).ShouldBeTrue(await DiagnosticsAsync(api, ct));

        using var response = await http.GetAsync(address + "/api/v1/identity-providers", ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(ct));
        response.Headers.TryGetValues(MinimumVersionHeader, out var versions).ShouldBeTrue();
        versions!.ShouldBe([ImageBuildFixture.TestVersion]);
    }

    [Fact]
    public async Task Web_ui_with_an_unreachable_upstream_serves_the_shell_and_answers_502_for_the_api()
    {
        var ct = TestContext.Current.CancellationToken;
        var address = await StartWebUiAsync("http://unreachable.invalid:8080", null, null, ct);
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

        using (var shell = await http.GetAsync(address + "/index.html", ct))
        {
            shell.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        using (var route = await http.GetAsync(address + "/matches/42", ct))
        {
            route.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        using var api = await http.GetAsync(address + "/api/v1/identity-providers", ct);
        api.StatusCode.ShouldBe(HttpStatusCode.BadGateway);
    }

    [Fact]
    public async Task Web_ui_runs_read_only_as_the_unprivileged_user()
    {
        var ct = TestContext.Current.CancellationToken;
        var image = await stamp.Images.BuildAsync(WebDockerfile);
        var id = await Probe.CreateAsync(image, stamp.NetworkName, WebEnvironment("http://unreachable.invalid:8080", null), ct: ct);
        await Probe.StartAsync(id, ct);
        var address = await AddressAsync(id, ct);
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        (await WaitForStatusAsync(http, address + "/index.html", HttpStatusCode.OK, id, ct)).ShouldBeTrue(await DiagnosticsAsync(id, ct));

        var uids = await Probe.ProcessUidsAsync(id, ct);
        uids.ShouldNotBeEmpty();
        uids.ShouldAllBe(uid => uid == "101");
        (await Probe.InspectContainerAsync(id, ct)).GetProperty("HostConfig").GetProperty("ReadonlyRootfs").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task Web_ui_with_an_arbitrary_non_root_user_still_serves_the_shell()
    {
        var ct = TestContext.Current.CancellationToken;
        var address = await StartWebUiAsync("http://unreachable.invalid:8080", null, "12345:0", ct);
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

        using var response = await http.GetAsync(address + "/index.html", ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [GeneratedRegex("""<(?:script|link)\b[^>]*\bintegrity="[^"]*"[^>]*>""", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex IntegrityTagRegex();

    [GeneratedRegex(@"([\w-]+)=""([^""]*)""", RegexOptions.CultureInvariant)]
    private static partial Regex AttributeRegex();

    private static string Integrity(byte[] content) => "sha384-" + Convert.ToBase64String(SHA384.HashData(content));

    private static string? Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? string.Join(", ", values) : null;

    private static void AssertSecurityHeaders(HttpResponseMessage response, string policy)
    {
        var path = response.RequestMessage?.RequestUri?.AbsolutePath;
        // The template leaves one insignificant space before ';' when no object storage origin is set.
        Header(response, "Content-Security-Policy")?.Replace(" ;", ";").ShouldBe(policy, path);
        Header(response, "X-Content-Type-Options").ShouldBe("nosniff", path);
        Header(response, "Referrer-Policy").ShouldBe("no-referrer", path);
        Header(response, "Permissions-Policy").ShouldBe("camera=(), microphone=(), geolocation=()", path);
    }

    private static Dictionary<string, string> WebEnvironment(string upstream, string? storageOrigin)
    {
        var environment = new Dictionary<string, string> { ["SOCALYTICS_API_UPSTREAM"] = upstream };
        if (storageOrigin is not null)
        {
            environment["SOCALYTICS_OBJECT_STORAGE_ORIGIN"] = storageOrigin;
        }

        return environment;
    }

    private async Task<string> StartApiAsync(CancellationToken ct)
    {
        var image = await stamp.Images.BuildAsync(ApiDockerfile);
        var id = await Probe.CreateAsync(image, stamp.NetworkName, stamp.ApiEnvironment(), alias: ApiAlias, ct: ct);
        await Probe.StartAsync(id, ct);
        return id;
    }

    private async Task<string> StartWebUiAsync(string upstream, string? storageOrigin, string? user, CancellationToken ct)
    {
        var image = await stamp.Images.BuildAsync(WebDockerfile);
        var id = await Probe.CreateAsync(image, stamp.NetworkName, WebEnvironment(upstream, storageOrigin), user, ct: ct);
        await Probe.StartAsync(id, ct);
        var address = await AddressAsync(id, ct);

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        (await WaitForStatusAsync(http, address + "/index.html", HttpStatusCode.OK, id, ct)).ShouldBeTrue(await DiagnosticsAsync(id, ct));
        return address;
    }

    private async Task<string> AddressAsync(string id, CancellationToken ct)
    {
        var inspection = await Probe.InspectContainerAsync(id, ct);
        var ip = inspection.GetProperty("NetworkSettings").GetProperty("Networks").GetProperty(stamp.NetworkName).GetProperty("IPAddress").GetString();
        ip.ShouldNotBeNullOrWhiteSpace();
        return $"http://{ip}:{Port}";
    }

    private async Task<bool> WaitForStatusAsync(HttpClient http, string url, HttpStatusCode expected, string containerId, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + ReadyWithin;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var response = await http.GetAsync(url, ct);
                if (response.StatusCode == expected)
                {
                    return true;
                }
            }
            catch (HttpRequestException)
            {
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
            }

            if (!await Probe.IsRunningAsync(containerId, ct))
            {
                return false;
            }

            await Task.Delay(500, ct);
        }

        return false;
    }

    private async Task<string> DiagnosticsAsync(string id, CancellationToken ct) =>
        $"Container {id} logs:\n{await Probe.LogsAsync(id, ct)}";
}
