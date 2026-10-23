using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Shouldly;
using SocAlytics.Platform.EndToEnd.Tests.Support;
using SocAlytics.Platform.Integration.Tests.Registry.Support;
using Xunit;

namespace SocAlytics.Platform.EndToEnd.Tests;

/// <summary>
/// The reference Analyst image is accepted by the Registry and carries the restrictions the Manager relies on
/// (FR-009, FR-011, FR-013; SC-004).
/// </summary>
[Collection("EndToEnd")]
public sealed class ReferenceAnalystImageTests
{
    private const string Prefix = "analysts/";
    private const string ManifestFile = "src/analysts/capabilities/reference-media-probe/analyst-manifest.json";
    private const string UnknownUser = "54321:54321";

    [Fact]
    public async Task The_registry_accepts_the_image_and_it_runs_under_the_Manager_restrictions()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var ct = cts.Token;

        var manifestBytes = await File.ReadAllBytesAsync(Path.Combine(ReferenceAnalystImage.FindRepositoryRoot(), ManifestFile), ct);
        var manifest = JsonNode.Parse(manifestBytes)!;

        var registry = new OciRegistryContainerFixture();
        await registry.InitializeAsync();
        PushedAnalystImage? image = null;
        try
        {
            var started = await EndToEndHarness.StartAsync(ct, new Dictionary<string, string?>
            {
                ["Registry:Images:RegistryHost"] = registry.RegistryHost,
                ["Registry:Images:RepositoryPrefix"] = Prefix,
                ["Registry:Images:UseHttp"] = "true",
            });
            if (started is null)
            {
                Assert.Skip("SoftHSM2 (libsofthsm2 and softhsm2-util) is required on Linux for the Manager's device key.");
                return;
            }

            await using var harness = started;
            image = await ReferenceAnalystImage.BuildAndPushAsync(registry.RegistryHost, Prefix, ct);

            await AssertRegisteredAsync(harness, image.Reference(registry.RegistryHost), image.Digest, manifest, ct);

            using var docker = new DockerEngine();
            await AssertImageAsync(docker, image.LocalName, manifest, manifestBytes, ct);
            await AssertRestrictedRunAsync(image.LocalName, ct);
        }
        finally
        {
            if (image is not null)
            {
                await ReferenceAnalystImage.RemoveAsync(image);
            }

            await registry.DisposeAsync();
        }
    }

    private static async Task AssertRegisteredAsync(EndToEndHarness harness, string reference, string digest, JsonNode manifest, CancellationToken ct)
    {
        using var response = await harness.Admin.SendAsync(HttpMethod.Post, "/api/v1/registry/analyst-images", new { reference }, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Created, text);

        var body = JsonDocument.Parse(text).RootElement;
        body.GetProperty("imageDigest").GetString().ShouldBe(digest);
        var capability = body.GetProperty("capability");
        capability.GetProperty("id").GetString().ShouldBe(ReferenceAnalystImage.CapabilityId);
        capability.GetProperty("version").GetString().ShouldBe("1.0.0");
        body.GetProperty("modelDigest").GetString().ShouldBe(ReferenceAnalystImage.ModelDigest);
        JsonNode.DeepEquals(JsonNode.Parse(body.GetProperty("manifest").GetRawText()), manifest)
            .ShouldBeTrue("The stored capability declaration differs from analyst-manifest.json.");
    }

    private static async Task AssertImageAsync(DockerEngine docker, string localName, JsonNode manifest, byte[] manifestBytes, CancellationToken ct)
    {
        var inspected = await docker.GetJsonAsync($"/images/{Uri.EscapeDataString(localName)}/json", ct);
        inspected.GetProperty("Architecture").GetString().ShouldBe("amd64");

        var config = inspected.GetProperty("Config");
        var user = config.GetProperty("User").GetString();
        user.ShouldNotBeNullOrEmpty();
        user.Split(':')[0].ShouldNotBe("0");
        user.ShouldNotBe("root");

        var labels = config.GetProperty("Labels");
        Label("io.socalytics.analyst-manifest.version").ShouldBe(manifest["manifestVersion"]!.GetValue<string>());
        Label("io.socalytics.analyst-manifest.sha256").ShouldBe(Convert.ToHexStringLower(SHA256.HashData(manifestBytes)));
        Label("io.socalytics.capability.id").ShouldBe(manifest["capability"]!["id"]!.GetValue<string>());
        Label("io.socalytics.capability.version").ShouldBe(manifest["capability"]!["version"]!.GetValue<string>());
        Label("io.socalytics.model.digest").ShouldBe(manifest["model"]!["digest"]!.GetValue<string>());

        string? Label(string name) => labels.GetProperty(name).GetString();
    }

    private static async Task AssertRestrictedRunAsync(string image, CancellationToken ct)
    {
        const string script = """
            test -s /opt/reference-media-probe/bin/LICENSE.txt || { echo "FFmpeg license notice is missing" >&2; exit 10; }
            command -v ffprobe >/dev/null && test -x /opt/reference-media-probe/bin/ffprobe || { echo "ffprobe is missing" >&2; exit 11; }
            /opt/reference-media-probe/venv/bin/python -m reference_media_probe
            status=$?
            test "$status" -eq 2 || { echo "the entry point exited with $status" >&2; exit 12; }
            """;

        using var runner = new ContainerRunner();
        var result = await runner.RunAsync(image, UnknownUser, ["/bin/sh", "-c"], [script], ct);
        result.ExitCode.ShouldBe(0, result.Logs);
        result.Changes.ShouldBeEmpty("The container wrote to its file system.");
    }

    /// <summary>Runs a container with the Manager's restrictions to its exit and returns its status, logs, and file system changes.</summary>
    private sealed class ContainerRunner : IDisposable
    {
        private readonly HttpClient _http = new(new SocketsHttpHandler
        {
            ConnectCallback = async (_, token) =>
            {
                var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                await socket.ConnectAsync(new UnixDomainSocketEndPoint("/var/run/docker.sock"), token);
                return new NetworkStream(socket, ownsSocket: true);
            },
        })
        {
            BaseAddress = new Uri("http://docker"),
            Timeout = TimeSpan.FromMinutes(5),
        };

        public async Task<RunResult> RunAsync(string image, string user, string[] entrypoint, string[] command, CancellationToken ct)
        {
            var body = JsonSerializer.Serialize(new
            {
                Image = image,
                User = user,
                Entrypoint = entrypoint,
                Cmd = command,
                HostConfig = new
                {
                    NetworkMode = "none",
                    ReadonlyRootfs = true,
                    CapDrop = new[] { "ALL" },
                    SecurityOpt = new[] { "no-new-privileges" },
                },
            });
            using var content = new StringContent(body, Encoding.UTF8, "application/json");
            using var created = await _http.PostAsync("/containers/create", content, ct);
            created.EnsureSuccessStatusCode();
            var id = JsonDocument.Parse(await created.Content.ReadAsStringAsync(ct)).RootElement.GetProperty("Id").GetString()!;
            try
            {
                using var started = await _http.PostAsync($"/containers/{id}/start", null, ct);
                started.EnsureSuccessStatusCode();
                using var waited = await _http.PostAsync($"/containers/{id}/wait", null, ct);
                waited.EnsureSuccessStatusCode();
                var exitCode = JsonDocument.Parse(await waited.Content.ReadAsStringAsync(ct)).RootElement.GetProperty("StatusCode").GetInt32();

                using var logs = await _http.GetAsync($"/containers/{id}/logs?stdout=1&stderr=1", ct);
                var text = Encoding.UTF8.GetString(await logs.Content.ReadAsByteArrayAsync(ct));

                using var changed = await _http.GetAsync($"/containers/{id}/changes", ct);
                changed.EnsureSuccessStatusCode();
                var changes = JsonDocument.Parse(await changed.Content.ReadAsStringAsync(ct)).RootElement;
                var paths = changes.ValueKind == JsonValueKind.Array
                    ? changes.EnumerateArray().Select(c => c.GetProperty("Path").GetString()!).ToList()
                    : [];
                return new RunResult(exitCode, text, paths);
            }
            finally
            {
                using var removed = await _http.DeleteAsync($"/containers/{id}?force=true", CancellationToken.None);
            }
        }

        public void Dispose() => _http.Dispose();
    }

    private sealed record RunResult(int ExitCode, string Logs, IReadOnlyList<string> Changes);
}
