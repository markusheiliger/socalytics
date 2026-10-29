using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SocAlytics.Platform.EndToEnd.Tests.Support;

/// <summary>Outcome of one digest lookup: <c>found</c>, <c>absent</c>, or <c>unavailable: &lt;reason&gt;</c>.</summary>
internal readonly record struct DigestLookup(string? Digest, string Outcome)
{
    private static readonly Regex ValidDigest = new("^sha256:[0-9a-f]{64}$", RegexOptions.CultureInvariant);

    public static DigestLookup Absent => new(null, "absent");

    public static DigestLookup Unavailable(string reason) => new(null, "unavailable: " + reason);

    /// <summary>A found digest, or unavailable when the value is not <c>sha256:</c> plus 64 lowercase hex characters.</summary>
    public static DigestLookup FromValue(string? value) => value is not null && ValidDigest.IsMatch(value)
        ? new DigestLookup(value, "found")
        : value is null ? Absent : Unavailable("malformed digest");
}

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

        var separator = name.IndexOf('/');
        var repository = separator < 0 ? name : name[(separator + 1)..];
        var result = await PushDigest.DetermineAsync(
            body,
            repository,
            tag,
            lookupCt => ToDigestAsync(LookupRegistryDigestAsync(repository, tag, lookupCt)),
            lookupCt => ToDigestAsync(LookupEngineDigestAsync(name, tag, lookupCt)),
            ct);
        return result.Succeeded ? result.Digest! : throw new InvalidOperationException(result.FailureMessage);
    }

    private const string UnavailablePrefix = "unavailable: ";

    private static async Task<string?> ToDigestAsync(Task<DigestLookup> lookup)
    {
        var result = await lookup;
        return result.Outcome.StartsWith(UnavailablePrefix, StringComparison.Ordinal)
            ? throw new InvalidOperationException(result.Outcome[UnavailablePrefix.Length..])
            : result.Digest;
    }

    /// <summary>The manifest digest the registry reports for <paramref name="repository"/> and <paramref name="tag"/> (<c>Docker-Content-Digest</c>).</summary>
    public async Task<DigestLookup> LookupRegistryDigestAsync(string repository, string tag, CancellationToken ct)
    {
        if (RegistryBaseAddress is null)
        {
            return DigestLookup.Unavailable("no registry address");
        }

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(EngineCallPolicy.RegistryLookupLimit);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(RegistryBaseAddress, $"/v2/{repository}/manifests/{tag}"));
            foreach (var type in ManifestMediaTypes)
            {
                request.Headers.Accept.ParseAdd(type);
            }

            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, limit.Token);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return DigestLookup.Absent;
            }

            if (!response.IsSuccessStatusCode)
            {
                return DigestLookup.Unavailable($"registry answered {(int)response.StatusCode}");
            }

            if (!response.Headers.TryGetValues("Docker-Content-Digest", out var values))
            {
                return DigestLookup.Absent;
            }

            return DigestLookup.FromValue(values.FirstOrDefault());
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            return DigestLookup.Unavailable(e is OperationCanceledException ? "registry lookup timed out" : e.Message);
        }
    }

    /// <summary>The digest in the engine's image record of <paramref name="name"/> that belongs to the pushed repository (<c>RepoDigests</c>).</summary>
    public async Task<DigestLookup> LookupEngineDigestAsync(string name, string tag, CancellationToken ct)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(EngineCallPolicy.RegistryLookupLimit);
        try
        {
            using var response = await _http.GetAsync($"/images/{Uri.EscapeDataString(name + ":" + tag)}/json", limit.Token);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return DigestLookup.Absent;
            }

            if (!response.IsSuccessStatusCode)
            {
                return DigestLookup.Unavailable($"engine answered {(int)response.StatusCode}");
            }

            using var record = JsonDocument.Parse(await response.Content.ReadAsStringAsync(limit.Token));
            if (!record.RootElement.TryGetProperty("RepoDigests", out var digests) || digests.ValueKind != JsonValueKind.Array)
            {
                return DigestLookup.Absent;
            }

            var prefix = name + "@";
            var value = digests.EnumerateArray()
                .Select(d => d.ValueKind == JsonValueKind.String ? d.GetString()! : string.Empty)
                .FirstOrDefault(d => d.StartsWith(prefix, StringComparison.Ordinal));
            return value is null ? DigestLookup.Absent : DigestLookup.FromValue(value[prefix.Length..]);
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            return DigestLookup.Unavailable(e is OperationCanceledException ? "engine lookup timed out" : e.Message);
        }
    }

    private static readonly string[] ManifestMediaTypes =
    [
        "application/vnd.oci.image.index.v1+json",
        "application/vnd.oci.image.manifest.v1+json",
        "application/vnd.docker.distribution.manifest.list.v2+json",
        "application/vnd.docker.distribution.manifest.v2+json",
    ];

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
