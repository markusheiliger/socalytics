using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace SocAlytics.Platform.EndToEnd.Tests.Support;

/// <summary>The few Docker Engine calls the end-to-end test needs, on the local engine socket.</summary>
internal sealed class DockerEngine : IDisposable
{
    private const string SocketPath = "/var/run/docker.sock";

    private readonly HttpClient _http;

    /// <summary>The engine on the local socket.</summary>
    public DockerEngine()
        : this(CreateSocketHandler(), null)
    {
    }

    /// <summary>
    /// An engine reached through <paramref name="handler"/>, so lookups can run against a fake endpoint;
    /// <paramref name="registryBaseAddress"/> is the registry the lookups address (none for the engine alone).
    /// </summary>
    public DockerEngine(HttpMessageHandler handler, Uri? registryBaseAddress)
    {
        RegistryBaseAddress = registryBaseAddress;
        _http = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://docker"),
            Timeout = TimeSpan.FromMinutes(5),
        };
    }

    public Uri? RegistryBaseAddress { get; }

    private static SocketsHttpHandler CreateSocketHandler() => new()
    {
        ConnectCallback = async (_, ct) =>
        {
            var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(SocketPath), ct);
            return new NetworkStream(socket, ownsSocket: true);
        },
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
