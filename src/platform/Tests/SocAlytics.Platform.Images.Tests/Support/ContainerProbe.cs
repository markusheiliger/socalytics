using System.Formats.Tar;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace SocAlytics.Platform.Images.Tests.Support;

/// <summary>The image configuration a test asserts on.</summary>
internal sealed record ImageConfiguration(
    string User,
    IReadOnlyDictionary<string, string> Labels,
    bool HasHealthcheck,
    IReadOnlyList<string> Environment,
    IReadOnlyList<string> History);

/// <summary>The outcome of a container run to completion.</summary>
internal sealed record ContainerRun(string Id, long ExitCode, string Logs, bool ReadonlyRootfs);

/// <summary>Starts containers with a read-only root filesystem through the local Docker Engine API and inspects images and containers.</summary>
internal sealed class ContainerProbe : IDisposable
{
    private readonly HttpClient _http;
    private readonly List<string> _containers = [];

    public ContainerProbe()
    {
        var socket = Environment.GetEnvironmentVariable("DOCKER_HOST") is { } host && host.StartsWith("unix://", StringComparison.Ordinal)
            ? host["unix://".Length..]
            : "/var/run/docker.sock";
        _http = new HttpClient(new SocketsHttpHandler
        {
            ConnectCallback = async (_, ct) =>
            {
                var s = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                await s.ConnectAsync(new UnixDomainSocketEndPoint(socket), ct);
                return new NetworkStream(s, ownsSocket: true);
            },
        })
        {
            BaseAddress = new Uri("http://docker"),
            Timeout = TimeSpan.FromMinutes(5),
        };
    }

    /// <summary>Creates a container with a read-only root filesystem and a tmpfs at <c>/tmp</c> on <paramref name="network"/>, without starting it.</summary>
    public async Task<string> CreateAsync(
        string image,
        string? network,
        IReadOnlyDictionary<string, string>? environment = null,
        string? user = null,
        string[]? command = null,
        string[]? entrypoint = null,
        string? alias = null,
        CancellationToken ct = default)
    {
        var body = new Dictionary<string, object?>
        {
            ["Image"] = image,
            ["Env"] = (environment ?? new Dictionary<string, string>()).Select(pair => $"{pair.Key}={pair.Value}").ToArray(),
            ["HostConfig"] = new Dictionary<string, object?>
            {
                ["ReadonlyRootfs"] = true,
                ["Tmpfs"] = new Dictionary<string, string> { ["/tmp"] = "rw,noexec,nosuid,size=64m" },
                ["NetworkMode"] = network ?? "default",
            },
        };
        if (user is not null)
        {
            body["User"] = user;
        }

        if (alias is not null && network is not null)
        {
            body["NetworkingConfig"] = new Dictionary<string, object?>
            {
                ["EndpointsConfig"] = new Dictionary<string, object?> { [network] = new Dictionary<string, object?> { ["Aliases"] = new[] { alias } } },
            };
        }

        if (command is not null)
        {
            body["Cmd"] = command;
        }

        if (entrypoint is not null)
        {
            body["Entrypoint"] = entrypoint;
        }

        var created = await SendJsonAsync(HttpMethod.Post, "/containers/create", body, ct);
        var id = created.GetProperty("Id").GetString()!;
        _containers.Add(id);
        return id;
    }

    public async Task StartAsync(string containerId, CancellationToken ct = default)
    {
        using var response = await _http.PostAsync($"/containers/{containerId}/start", null, ct);
        await EnsureSuccessAsync(response, ct);
    }

    /// <summary>Runs the image to completion (a bounded wait) and returns its exit code, logs, and read-only flag.</summary>
    public async Task<ContainerRun> RunToCompletionAsync(
        string image,
        string? network,
        IReadOnlyDictionary<string, string>? environment = null,
        string? user = null,
        string[]? command = null,
        TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        var id = await CreateAsync(image, network, environment, user, command, ct: ct);
        await StartAsync(id, ct);

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout ?? TimeSpan.FromMinutes(2));
        using var waited = await _http.PostAsync($"/containers/{id}/wait?condition=not-running", null, limit.Token);
        await EnsureSuccessAsync(waited, ct);
        var exit = JsonDocument.Parse(await waited.Content.ReadAsStringAsync(ct)).RootElement.GetProperty("StatusCode").GetInt64();

        var inspection = await InspectContainerAsync(id, ct);
        return new ContainerRun(id, exit, await LogsAsync(id, ct), inspection.GetProperty("HostConfig").GetProperty("ReadonlyRootfs").GetBoolean());
    }

    public Task<JsonElement> InspectContainerAsync(string containerId, CancellationToken ct = default) =>
        GetJsonAsync($"/containers/{containerId}/json", ct);

    public async Task<bool> IsRunningAsync(string containerId, CancellationToken ct = default) =>
        (await InspectContainerAsync(containerId, ct)).GetProperty("State").GetProperty("Running").GetBoolean();

    public async Task<string> LogsAsync(string containerId, CancellationToken ct = default)
    {
        using var response = await _http.GetAsync($"/containers/{containerId}/logs?stdout=1&stderr=1", ct);
        await EnsureSuccessAsync(response, ct);
        var bytes = await response.Content.ReadAsByteArrayAsync(ct);
        var text = new StringBuilder();
        // Multiplexed stream: an 8-byte header (stream, 3 zero bytes, big-endian length) precedes each frame.
        for (var offset = 0; offset + 8 <= bytes.Length;)
        {
            var length = (bytes[offset + 4] << 24) | (bytes[offset + 5] << 16) | (bytes[offset + 6] << 8) | bytes[offset + 7];
            offset += 8;
            length = Math.Min(length, bytes.Length - offset);
            text.Append(Encoding.UTF8.GetString(bytes, offset, length));
            offset += length;
        }

        return text.ToString();
    }

    /// <summary>The UIDs of the processes of a running container, from the Docker Engine <c>top</c> operation.</summary>
    public async Task<IReadOnlyList<string>> ProcessUidsAsync(string containerId, CancellationToken ct = default)
    {
        // The engine requires a PID column in the ps output.
        var top = await GetJsonAsync($"/containers/{containerId}/top?ps_args=-eo%20pid,uid", ct);
        var uidColumn = top.GetProperty("Titles").EnumerateArray().Select(t => t.GetString()).ToList().IndexOf("UID");
        if (uidColumn < 0)
        {
            throw new InvalidOperationException("The Docker Engine top output has no UID column: " + top.GetProperty("Titles"));
        }

        return top.GetProperty("Processes").EnumerateArray()
            .Select(row => row[uidColumn].GetString()!.Trim())
            .ToList();
    }

    public async Task<ImageConfiguration> ReadImageAsync(string image, CancellationToken ct = default)
    {
        var inspection = await GetJsonAsync($"/images/{Uri.EscapeDataString(image)}/json", ct);
        var config = inspection.GetProperty("Config");

        var labels = config.TryGetProperty("Labels", out var labelValue) && labelValue.ValueKind == JsonValueKind.Object
            ? labelValue.EnumerateObject().ToDictionary(property => property.Name, property => property.Value.GetString() ?? string.Empty)
            : [];
        var environment = config.TryGetProperty("Env", out var env) && env.ValueKind == JsonValueKind.Array
            ? env.EnumerateArray().Select(item => item.GetString()!).ToList()
            : [];
        var healthcheck = config.TryGetProperty("Healthcheck", out var health) && health.ValueKind == JsonValueKind.Object
            && !(health.TryGetProperty("Test", out var test) && test.ValueKind == JsonValueKind.Array
                && test.GetArrayLength() == 1 && test[0].GetString() == "NONE");

        var history = (await GetJsonAsync($"/images/{Uri.EscapeDataString(image)}/history", ct)).EnumerateArray()
            .Select(layer => layer.GetProperty("CreatedBy").GetString() ?? string.Empty)
            .ToList();

        return new ImageConfiguration(
            config.TryGetProperty("User", out var user) ? user.GetString() ?? string.Empty : string.Empty,
            labels,
            healthcheck,
            environment,
            history);
    }

    /// <summary>The paths of every file in the image, read from the exported filesystem of a container that is never started.</summary>
    public async Task<IReadOnlyList<string>> ListImageFilesAsync(string image, CancellationToken ct = default)
    {
        var id = await CreateAsync(image, network: "none", ct: ct);
        using var response = await _http.GetAsync($"/containers/{id}/export", HttpCompletionOption.ResponseHeadersRead, ct);
        await EnsureSuccessAsync(response, ct);
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        await using var reader = new TarReader(stream);

        var files = new List<string>();
        while (await reader.GetNextEntryAsync(copyData: false, ct) is { } entry)
        {
            if (entry.EntryType is not TarEntryType.Directory)
            {
                files.Add("/" + entry.Name.TrimStart('/'));
            }
        }

        return files;
    }

    public void Dispose()
    {
        foreach (var id in _containers)
        {
            try
            {
                using var response = _http.DeleteAsync($"/containers/{id}?force=true&v=true").GetAwaiter().GetResult();
            }
            catch (HttpRequestException)
            {
            }
        }

        _http.Dispose();
    }

    private async Task<JsonElement> GetJsonAsync(string path, CancellationToken ct)
    {
        using var response = await _http.GetAsync(path, ct);
        await EnsureSuccessAsync(response, ct);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct)).RootElement.Clone();
    }

    private async Task<JsonElement> SendJsonAsync(HttpMethod method, string path, object body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, path)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };
        using var response = await _http.SendAsync(request, ct);
        await EnsureSuccessAsync(response, ct);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct)).RootElement.Clone();
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"The Docker Engine returned {(int)response.StatusCode} for {response.RequestMessage?.RequestUri?.PathAndQuery}: {await response.Content.ReadAsStringAsync(ct)}");
        }
    }
}
