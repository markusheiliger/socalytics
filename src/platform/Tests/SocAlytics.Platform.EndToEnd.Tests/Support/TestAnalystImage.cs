using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Images;

namespace SocAlytics.Platform.EndToEnd.Tests.Support;

/// <summary>A pushed Test Analyst image: the registry repository and its manifest digest (<c>sha256:</c> form).</summary>
internal sealed record PushedAnalystImage(string Repository, string Digest, string LocalName)
{
    public string Reference(string registryHost) => $"{registryHost}/{Repository}@{Digest}";
}

/// <summary>Builds the Test Analyst image with Testcontainers and pushes it to a registry through the Docker Engine API.</summary>
internal static class TestAnalystImage
{
    public const string CapabilityId = "e2e-docker-analyst";
    public const string ProfileId = "e2e-docker-profile";
    public const string ModelDigest = "sha-256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    public static string CapabilityFor(string mode) => $"{CapabilityId}-{mode}";

    public static string ProfileFor(string mode) => $"{ProfileId}-{mode}";

    /// <summary>
    /// Builds the image for <paramref name="mode"/>. The default mode <c>succeed</c> is the capability <see cref="CapabilityId"/>;
    /// every other mode is imported as its own capability with a manifest that allows the short limits of the failure tests.
    /// </summary>
    public static async Task<PushedAnalystImage> BuildAndPushAsync(string registryHost, string repositoryPrefix, CancellationToken ct, string mode = "succeed")
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "TestAnalyst");
        var capabilityId = CapabilityId;
        string? contextDirectory = null;
        var manifestVersion = "1.0.0";
        if (mode != "succeed")
        {
            capabilityId = CapabilityFor(mode);
            contextDirectory = Path.Combine(Path.GetTempPath(), $"socalytics-e2e-analyst-{Guid.NewGuid():N}");
            Directory.CreateDirectory(contextDirectory);
            foreach (var file in new[] { "Dockerfile", "analyst.py" })
            {
                File.Copy(Path.Combine(directory, file), Path.Combine(contextDirectory, file));
            }

            var manifest = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "analyst-manifest.json"), ct))!;
            manifest["capability"]!["id"] = capabilityId;
            manifest["compatibleProfiles"] = new System.Text.Json.Nodes.JsonArray(ProfileFor(mode));
            var requirements = manifest["requirements"]!;
            requirements["minMaxAttempts"] = 1;
            requirements["minLeaseSeconds"] = 1;
            requirements["minStaleTimeoutSeconds"] = 1;
            if (mode is "media" or "payload-invalid")
            {
                manifest["videoAccess"] = "required";
                manifest["capability"]!["tier"] = "low-level";
                manifest["capability"]!["executionScope"] = "segment";
            }

            if (mode == "payload-invalid")
            {
                // The registered schema is stricter than the facts the Analyst writes (a media observation has a positive size).
                manifestVersion = "1.2.0";
                manifest["manifestVersion"] = manifestVersion;
                manifest["output"]!["factPayloadSchemas"] = System.Text.Json.Nodes.JsonNode.Parse("""
                    {
                      "e2e-media-observation": {
                        "type": "object",
                        "required": ["sizeBytes"],
                        "properties": { "sizeBytes": { "type": "integer", "maximum": 0 } }
                      }
                    }
                    """);
            }

            await File.WriteAllTextAsync(Path.Combine(contextDirectory, "analyst-manifest.json"), manifest.ToJsonString(), ct);
            directory = contextDirectory;
        }

        var manifestHash = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(directory, "analyst-manifest.json"), ct)));
        var repository = repositoryPrefix + capabilityId;
        var localName = $"{registryHost}/{repository}:{Guid.NewGuid():N}";

        var image = new ImageFromDockerfileBuilder()
            .WithDockerfileDirectory(directory)
            .WithDockerfile("Dockerfile")
            .WithBuildArgument("MODE", mode)
            .WithBuildArgument("MANIFEST_SHA256", manifestHash)
            .WithBuildArgument("CAPABILITY_ID", capabilityId)
            .WithBuildArgument("MANIFEST_VERSION", manifestVersion)
            .WithName(new DockerImage(localName))
            .WithDeleteIfExists(false)
            .Build();
        try
        {
            await image.CreateAsync(ct);
        }
        finally
        {
            if (contextDirectory is not null)
            {
                Directory.Delete(contextDirectory, true);
            }
        }

        var tag = localName[(localName.LastIndexOf(':') + 1)..];
        var name = localName[..localName.LastIndexOf(':')];
        using var docker = new DockerEngine();
        var digest = await docker.PushAsync(name, tag, ct);
        return new PushedAnalystImage(repository, digest, localName);
    }

    public static async Task RemoveAsync(PushedAnalystImage image)
    {
        using var docker = new DockerEngine();
        await docker.DeleteImageAsync(image.LocalName);
    }
}

/// <summary>The few Docker Engine calls the end-to-end test needs, on the local engine socket.</summary>
internal sealed class DockerEngine : IDisposable
{
    private const string SocketPath = "/var/run/docker.sock";

    private readonly HttpClient _http = new(new SocketsHttpHandler
    {
        ConnectCallback = async (_, ct) =>
        {
            var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(SocketPath), ct);
            return new NetworkStream(socket, ownsSocket: true);
        },
    })
    {
        BaseAddress = new Uri("http://docker"),
        Timeout = TimeSpan.FromMinutes(5),
    };

    public async Task<string> PushAsync(string name, string tag, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/images/{Uri.EscapeDataString(name)}/push?tag={tag}");
        request.Headers.Add("X-Registry-Auth", Convert.ToBase64String(Encoding.UTF8.GetBytes("{}")));
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(ct);
        string? digest = null;
        foreach (var line in body.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            using var document = JsonDocument.Parse(line);
            if (document.RootElement.TryGetProperty("errorDetail", out var error))
            {
                throw new InvalidOperationException("Pushing the Test Analyst image failed: " + error.GetRawText());
            }

            if (document.RootElement.TryGetProperty("aux", out var aux) && aux.TryGetProperty("Digest", out var value))
            {
                digest = value.GetString();
            }
        }

        return digest ?? throw new InvalidOperationException("The push did not report a manifest digest:\n" + body);
    }

    public async Task DeleteImageAsync(string name)
    {
        using var response = await _http.DeleteAsync($"/images/{Uri.EscapeDataString(name)}?force=true");
    }

    public async Task<JsonElement> GetJsonAsync(string path, CancellationToken ct)
    {
        using var response = await _http.GetAsync(path, ct);
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct)).RootElement.Clone();
    }

    /// <summary>The containers (running or not) that carry the label <c>key=value</c>.</summary>
    public async Task<IReadOnlyList<string>> ListContainersAsync(string label, CancellationToken ct)
    {
        var filters = Uri.EscapeDataString(JsonSerializer.Serialize(new Dictionary<string, string[]> { ["label"] = [label] }));
        var listed = await GetJsonAsync($"/containers/json?all=1&filters={filters}", ct);
        return listed.EnumerateArray().Select(c => c.GetProperty("Id").GetString()!).ToList();
    }

    /// <summary>Creates and starts a container that only sleeps, with the given labels (none for an unlabelled one).</summary>
    public async Task<string> StartSleepingContainerAsync(string image, IReadOnlyDictionary<string, string> labels, CancellationToken ct)
    {
        var body = JsonSerializer.Serialize(new
        {
            Image = image,
            Entrypoint = new[] { "sleep" },
            Cmd = new[] { "3600" },
            Labels = labels,
            HostConfig = new { NetworkMode = "none" },
        });
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var created = await _http.PostAsync("/containers/create", content, ct);
        created.EnsureSuccessStatusCode();
        var id = JsonDocument.Parse(await created.Content.ReadAsStringAsync(ct)).RootElement.GetProperty("Id").GetString()!;
        using var started = await _http.PostAsync($"/containers/{id}/start", null, ct);
        started.EnsureSuccessStatusCode();
        return id;
    }

    public async Task<bool> IsRunningAsync(string containerId, CancellationToken ct)
    {
        using var response = await _http.GetAsync($"/containers/{containerId}/json", ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return false;
        }

        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct)).RootElement.GetProperty("State").GetProperty("Running").GetBoolean();
    }

    public async Task<bool> ExistsAsync(string containerId, CancellationToken ct)
    {
        using var response = await _http.GetAsync($"/containers/{containerId}/json", ct);
        return response.StatusCode != System.Net.HttpStatusCode.NotFound;
    }

    public async Task RemoveContainerAsync(string containerId)
    {
        using var response = await _http.DeleteAsync($"/containers/{containerId}?force=true");
    }

    public void Dispose() => _http.Dispose();
}
