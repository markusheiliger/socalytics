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

    public static async Task<PushedAnalystImage> BuildAndPushAsync(string registryHost, string repositoryPrefix, CancellationToken ct, string mode = "succeed")
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "TestAnalyst");
        var manifestHash = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(directory, "analyst-manifest.json"), ct)));
        var repository = repositoryPrefix + CapabilityId;
        var localName = $"{registryHost}/{repository}:{Guid.NewGuid():N}";

        var image = new ImageFromDockerfileBuilder()
            .WithDockerfileDirectory(directory)
            .WithDockerfile("Dockerfile")
            .WithBuildArgument("MODE", mode)
            .WithBuildArgument("MANIFEST_SHA256", manifestHash)
            .WithName(new DockerImage(localName))
            .WithDeleteIfExists(false)
            .Build();
        await image.CreateAsync(ct);

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

    public void Dispose() => _http.Dispose();
}
