using System.Globalization;
using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace SocAlytics.Analysts.Manager.Runtime.Docker;

public enum DockerErrorKind
{
    Unreachable,
    EndpointRefused,
    UnsupportedVersion,
    Unauthorized,
    NotFound,
    Conflict,
    BadRequest,
    ServerError,
    Failed,
}

/// <summary>Never carries a registry credential or a response body that could hold one.</summary>
public sealed class DockerEngineException(DockerErrorKind kind, string message, HttpStatusCode? status = null, Exception? inner = null)
    : Exception(message, inner)
{
    public DockerErrorKind Kind { get; } = kind;

    public HttpStatusCode? Status { get; } = status;
}

public sealed record DockerVersionInfo(
    string? PlatformName, string? Version, string? ApiVersion, string? MinApiVersion, string? Os, string? Arch);

public sealed record DockerInfo(string? OsType, string? OperatingSystem, int Ncpu, long MemTotal);

public sealed record DockerImageInfo(string Id, string? Os, string? Architecture, long Size);

public sealed record DockerContainerState(
    string? Status, bool Running, int ExitCode, bool OomKilled, string? Error, DateTimeOffset? StartedAt, DateTimeOffset? FinishedAt);

public sealed record DockerContainerSummary(string Id, string? State, IReadOnlyDictionary<string, string> Labels);

public sealed record DockerWaitResult(long StatusCode, string? Error);

public sealed record DockerLogOutput(string Stdout, string Stderr);

/// <summary>Minimal Docker Engine API client over a local endpoint; the API version is negotiated once and pinned into every request path.</summary>
public sealed partial class DockerEngineClient : IDisposable
{
    public const string MinimumApiVersion = "1.43";
    public const string MaximumApiVersion = "1.47";

    private static readonly Version Minimum = Version.Parse(MinimumApiVersion);
    private static readonly Version Maximum = Version.Parse(MaximumApiVersion);

    private readonly HttpClient _http;
    private readonly ILogger? _logger;
    private readonly SemaphoreSlim _negotiation = new(1, 1);
    private string? _prefix;

    public DockerEngineClient(HttpClient http, ILogger? logger = null)
    {
        _http = http;
        _logger = logger;
    }

    /// <summary>The negotiated API version, or null before the first call.</summary>
    public string? ApiVersion => _prefix?.TrimStart('/').TrimStart('v');

    /// <summary>Builds a client for an allowed endpoint; a refused endpoint throws before any connection exists.</summary>
    public static DockerEngineClient Create(DockerEndpointDecision endpoint, ILogger? logger = null)
    {
        if (!endpoint.Allowed || endpoint.Path is null)
        {
            throw new DockerEngineException(DockerErrorKind.EndpointRefused, $"{endpoint.Reason} {endpoint.Remediation}".Trim());
        }

        var path = endpoint.Path;
        var kind = endpoint.Kind;
        var handler = new SocketsHttpHandler
        {
            ConnectCallback = async (_, cancellationToken) =>
            {
                if (kind == DockerEndpointKind.NamedPipe)
                {
                    var pipe = new NamedPipeClientStream(".", path, PipeDirection.InOut, PipeOptions.Asynchronous);
                    try
                    {
                        await pipe.ConnectAsync(cancellationToken);
                        return pipe;
                    }
                    catch
                    {
                        await pipe.DisposeAsync();
                        throw;
                    }
                }

                var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                try
                {
                    await socket.ConnectAsync(new UnixDomainSocketEndPoint(path), cancellationToken);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            },
        };

        return new DockerEngineClient(
            new HttpClient(handler) { BaseAddress = new Uri("http://localhost"), Timeout = Timeout.InfiniteTimeSpan }, logger);
    }

    public async Task<bool> PingAsync(CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Get, "/_ping", null, null, cancellationToken);
        return response.IsSuccessStatusCode;
    }

    public async Task<DockerVersionInfo> GetVersionAsync(CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Get, "/version", null, null, cancellationToken);
        var node = await ReadJsonAsync(response, cancellationToken);
        var platform = node["Platform"]?["Name"]?.GetValue<string>();
        return new DockerVersionInfo(
            platform, node["Version"]?.GetValue<string>(), node["ApiVersion"]?.GetValue<string>(),
            node["MinAPIVersion"]?.GetValue<string>(), node["Os"]?.GetValue<string>(), node["Arch"]?.GetValue<string>());
    }

    public async Task<DockerInfo> GetInfoAsync(CancellationToken cancellationToken)
    {
        using var response = await SendVersionedAsync(HttpMethod.Get, "/info", null, null, cancellationToken);
        var node = await ReadJsonAsync(response, cancellationToken);
        return new DockerInfo(
            node["OSType"]?.GetValue<string>(), node["OperatingSystem"]?.GetValue<string>(),
            node["NCPU"]?.GetValue<int>() ?? 0, node["MemTotal"]?.GetValue<long>() ?? 0);
    }

    /// <summary>Pulls <paramref name="imageReference"/> (a digest reference); <paramref name="registryToken"/> becomes <c>X-Registry-Auth</c> and is never logged.</summary>
    public async Task PullImageAsync(string imageReference, string? registryToken, CancellationToken cancellationToken)
    {
        EnsureName(imageReference);
        var headers = RegistryAuthHeader(registryToken);
        using var response = await SendVersionedAsync(
            HttpMethod.Post, $"/images/create?fromImage={Uri.EscapeDataString(imageReference)}", null, headers, cancellationToken,
            logTarget: "/images/create");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            JsonNode? node;
            try
            {
                node = JsonNode.Parse(line);
            }
            catch (JsonException)
            {
                continue;
            }

            if (node?["error"]?.GetValue<string>() is { Length: > 0 } error)
            {
                var kind = error.Contains("unauthorized", StringComparison.OrdinalIgnoreCase) || error.Contains("denied", StringComparison.OrdinalIgnoreCase)
                    ? DockerErrorKind.Unauthorized
                    : error.Contains("not found", StringComparison.OrdinalIgnoreCase) || error.Contains("manifest unknown", StringComparison.OrdinalIgnoreCase)
                        ? DockerErrorKind.NotFound
                        : DockerErrorKind.Failed;
                throw new DockerEngineException(kind, "The image pull failed.");
            }
        }
    }

    public async Task<DockerImageInfo?> InspectImageAsync(string digestReference, CancellationToken cancellationToken)
    {
        EnsureName(digestReference);
        using var response = await SendVersionedAsync(
            HttpMethod.Get, $"/images/{digestReference}/json", null, null, cancellationToken, allowNotFound: true);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        var node = await ReadJsonAsync(response, cancellationToken);
        return new DockerImageInfo(
            node["Id"]?.GetValue<string>() ?? string.Empty, node["Os"]?.GetValue<string>(),
            node["Architecture"]?.GetValue<string>(), node["Size"]?.GetValue<long>() ?? 0);
    }

    /// <summary>Returns false when the image does not exist.</summary>
    public async Task<bool> RemoveImageAsync(string digestReference, CancellationToken cancellationToken)
    {
        EnsureName(digestReference);
        using var response = await SendVersionedAsync(
            HttpMethod.Delete, $"/images/{digestReference}", null, null, cancellationToken, allowNotFound: true);
        return response.StatusCode != HttpStatusCode.NotFound;
    }

    /// <summary>Creates a container from the Engine API create body and returns its id.</summary>
    public async Task<string> CreateContainerAsync(JsonObject body, string? name, CancellationToken cancellationToken)
    {
        var query = name is null ? string.Empty : $"?name={Uri.EscapeDataString(name)}";
        using var response = await SendVersionedAsync(
            HttpMethod.Post, $"/containers/create{query}", body.ToJsonString(), null, cancellationToken, logTarget: "/containers/create");
        var node = await ReadJsonAsync(response, cancellationToken);
        return node["Id"]?.GetValue<string>()
            ?? throw new DockerEngineException(DockerErrorKind.Failed, "The runtime returned no container id.");
    }

    /// <summary>An already started container is not an error.</summary>
    public async Task StartContainerAsync(string id, CancellationToken cancellationToken)
    {
        EnsureName(id);
        using var response = await SendVersionedAsync(
            HttpMethod.Post, $"/containers/{id}/start", null, null, cancellationToken, allowNotModified: true);
    }

    public async Task<DockerWaitResult> WaitContainerAsync(string id, CancellationToken cancellationToken)
    {
        EnsureName(id);
        using var response = await SendVersionedAsync(
            HttpMethod.Post, $"/containers/{id}/wait?condition=not-running", null, null, cancellationToken);
        var node = await ReadJsonAsync(response, cancellationToken);
        return new DockerWaitResult(
            node["StatusCode"]?.GetValue<long>() ?? -1, node["Error"]?["Message"]?.GetValue<string>());
    }

    public async Task<DockerContainerState?> InspectContainerAsync(string id, CancellationToken cancellationToken)
    {
        EnsureName(id);
        using var response = await SendVersionedAsync(
            HttpMethod.Get, $"/containers/{id}/json", null, null, cancellationToken, allowNotFound: true);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        var node = await ReadJsonAsync(response, cancellationToken);
        var state = node["State"] ?? new JsonObject();
        return new DockerContainerState(
            state["Status"]?.GetValue<string>(), state["Running"]?.GetValue<bool>() ?? false,
            state["ExitCode"]?.GetValue<int>() ?? 0, state["OOMKilled"]?.GetValue<bool>() ?? false,
            state["Error"]?.GetValue<string>(), ParseTime(state["StartedAt"]), ParseTime(state["FinishedAt"]));
    }

    /// <summary>Reads the demultiplexed output of a container created without a TTY, at most <paramref name="tail"/> lines.</summary>
    public async Task<DockerLogOutput> GetLogsAsync(string id, int tail, CancellationToken cancellationToken)
    {
        EnsureName(id);
        using var response = await SendVersionedAsync(
            HttpMethod.Get, $"/containers/{id}/logs?stdout=1&stderr=1&tail={tail.ToString(CultureInfo.InvariantCulture)}",
            null, null, cancellationToken);
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        return Demultiplex(bytes);
    }

    /// <summary>A container that is already stopped is not an error.</summary>
    public async Task StopContainerAsync(string id, TimeSpan grace, CancellationToken cancellationToken)
    {
        EnsureName(id);
        var seconds = Math.Max(0, (int)grace.TotalSeconds).ToString(CultureInfo.InvariantCulture);
        using var response = await SendVersionedAsync(
            HttpMethod.Post, $"/containers/{id}/stop?t={seconds}", null, null, cancellationToken,
            allowNotModified: true, allowNotFound: true);
    }

    public async Task RemoveContainerAsync(string id, bool force, CancellationToken cancellationToken)
    {
        EnsureName(id);
        using var response = await SendVersionedAsync(
            HttpMethod.Delete, $"/containers/{id}?force={(force ? "true" : "false")}&v=true", null, null, cancellationToken,
            allowNotFound: true);
    }

    public async Task<IReadOnlyList<DockerContainerSummary>> ListContainersAsync(
        IReadOnlyCollection<string> labelFilters, CancellationToken cancellationToken)
    {
        var filters = new JsonObject { ["label"] = new JsonArray(labelFilters.Select(l => (JsonNode)JsonValue.Create(l)!).ToArray()) };
        using var response = await SendVersionedAsync(
            HttpMethod.Get, $"/containers/json?all=true&filters={Uri.EscapeDataString(filters.ToJsonString())}",
            null, null, cancellationToken);
        var node = await ReadJsonAsync(response, cancellationToken);
        var result = new List<DockerContainerSummary>();
        foreach (var item in node.AsArray())
        {
            var labels = new Dictionary<string, string>(StringComparer.Ordinal);
            if (item?["Labels"] is JsonObject labelObject)
            {
                foreach (var (key, value) in labelObject)
                {
                    labels[key] = value?.GetValue<string>() ?? string.Empty;
                }
            }

            result.Add(new DockerContainerSummary(
                item?["Id"]?.GetValue<string>() ?? string.Empty, item?["State"]?.GetValue<string>(), labels));
        }

        return result;
    }

    public void Dispose()
    {
        _negotiation.Dispose();
        _http.Dispose();
    }

    private async Task EnsureNegotiatedAsync(CancellationToken cancellationToken)
    {
        if (_prefix is not null)
        {
            return;
        }

        await _negotiation.WaitAsync(cancellationToken);
        try
        {
            if (_prefix is not null)
            {
                return;
            }

            var info = await GetVersionAsync(cancellationToken);
            if (!Version.TryParse(info.ApiVersion, out var server) || server < Minimum)
            {
                throw new DockerEngineException(
                    DockerErrorKind.UnsupportedVersion,
                    $"The container runtime API version {info.ApiVersion ?? "unknown"} is older than {MinimumApiVersion}.");
            }

            var pinned = server > Maximum ? Maximum : server;
            _prefix = $"/v{pinned.ToString(2)}";
        }
        finally
        {
            _negotiation.Release();
        }
    }

    private async Task<HttpResponseMessage> SendVersionedAsync(
        HttpMethod method, string path, string? json, Dictionary<string, string>? headers, CancellationToken cancellationToken,
        bool allowNotFound = false, bool allowNotModified = false, string? logTarget = null)
    {
        await EnsureNegotiatedAsync(cancellationToken);
        return await SendAsync(method, _prefix + path, json, headers, cancellationToken, allowNotFound, allowNotModified, logTarget ?? _prefix + path);
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method, string path, string? json, Dictionary<string, string>? headers, CancellationToken cancellationToken,
        bool allowNotFound = false, bool allowNotModified = false, string? logTarget = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (json is not null)
        {
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        if (headers is not null)
        {
            foreach (var (name, value) in headers)
            {
                request.Headers.TryAddWithoutValidation(name, value);
            }
        }

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or SocketException or TimeoutException)
        {
            throw new DockerEngineException(DockerErrorKind.Unreachable, "The container runtime endpoint is not reachable.", null, ex);
        }

        var target = logTarget ?? path;
        _logger?.LogDebug("Docker {Method} {Path} returned {Status}.", method.Method, target, (int)response.StatusCode);
        if (response.IsSuccessStatusCode
            || (allowNotFound && response.StatusCode == HttpStatusCode.NotFound)
            || (allowNotModified && response.StatusCode == HttpStatusCode.NotModified))
        {
            return response;
        }

        var status = response.StatusCode;
        var message = await ReadErrorMessageAsync(response, cancellationToken);
        response.Dispose();
        var kind = status switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => DockerErrorKind.Unauthorized,
            HttpStatusCode.NotFound => DockerErrorKind.NotFound,
            HttpStatusCode.Conflict => DockerErrorKind.Conflict,
            HttpStatusCode.BadRequest => DockerErrorKind.BadRequest,
            >= HttpStatusCode.InternalServerError => DockerErrorKind.ServerError,
            _ => DockerErrorKind.Failed,
        };
        throw new DockerEngineException(kind, $"The container runtime refused {method.Method} {target} with {(int)status}: {message}", status);
    }

    private static async Task<string> ReadErrorMessageAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            return JsonNode.Parse(body)?["message"]?.GetValue<string>() ?? "no details";
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            return "no details";
        }
    }

    private static async Task<JsonNode> ReadJsonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        try
        {
            return JsonNode.Parse(body) ?? throw new DockerEngineException(DockerErrorKind.Failed, "The runtime returned an empty response.");
        }
        catch (JsonException ex)
        {
            throw new DockerEngineException(DockerErrorKind.Failed, "The runtime returned an unreadable response.", response.StatusCode, ex);
        }
    }

    private static Dictionary<string, string>? RegistryAuthHeader(string? token)
    {
        if (string.IsNullOrEmpty(token))
        {
            return null;
        }

        var json = new JsonObject { ["registrytoken"] = token }.ToJsonString();
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).Replace('+', '-').Replace('/', '_');
        return new Dictionary<string, string> { ["X-Registry-Auth"] = encoded };
    }

    private static DateTimeOffset? ParseTime(JsonNode? node) =>
        node?.GetValue<string>() is { } text
        && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var time)
        && time.Year > 1
            ? time
            : null;

    private static DockerLogOutput Demultiplex(byte[] bytes)
    {
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var offset = 0;
        while (offset + 8 <= bytes.Length)
        {
            var stream = bytes[offset];
            var length = (bytes[offset + 4] << 24) | (bytes[offset + 5] << 16) | (bytes[offset + 6] << 8) | bytes[offset + 7];
            offset += 8;
            if (length < 0 || offset + length > bytes.Length)
            {
                length = bytes.Length - offset;
            }

            (stream == 2 ? stderr : stdout).Append(Encoding.UTF8.GetString(bytes, offset, length));
            offset += length;
        }

        return new DockerLogOutput(stdout.ToString(), stderr.ToString());
    }

    private static void EnsureName(string value)
    {
        if (!SafeName().IsMatch(value))
        {
            throw new ArgumentException("The image reference or container id contains unsupported characters.", nameof(value));
        }
    }

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_.:/@-]*$")]
    private static partial Regex SafeName();
}
