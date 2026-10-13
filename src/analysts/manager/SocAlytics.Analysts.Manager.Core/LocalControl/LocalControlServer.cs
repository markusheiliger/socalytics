using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SocAlytics.Analysts.Manager.Hosting;
using SocAlytics.Analysts.Manager.LocalState;
using SocAlytics.Analysts.Manager.Registration;

namespace SocAlytics.Analysts.Manager.LocalControl;

/// <summary>Owner-only Unix domain socket server; never a network listener.</summary>
public sealed class LocalControlServer : IDisposable
{
    private static readonly HashSet<string> SchemaErrorCodes =
    [
        "invalid-request", "unsupported-protocol-version", "invalid-registration-state", "invalid-operating-state",
        "no-supported-key-store", "insecure-endpoint", "stamp-mismatch", "platform-unreachable", "pairing-refused",
        "operation-in-progress", "invalid-pause-duration", "autostart-failed", "software-key-not-allowed", "invalid-concurrency",
    ];

    private readonly Socket _listener;
    private readonly PeerCredentialVerifier _verifier;
    private readonly CancellationTokenSource _stop = new();
    private IManagerControl? _control;
    private Task? _loop;

    private LocalControlServer(Socket listener, string path, PeerCredentialVerifier verifier)
    {
        _listener = listener;
        _verifier = verifier;
        Path = path;
    }

    public string Path { get; }

    /// <summary>Binds the socket path; throws <see cref="SocketException"/> when the path is in use.</summary>
    public static LocalControlServer Bind(string path, PeerCredentialVerifier? verifier = null)
    {
        OwnerOnlyFiles.EnsureDirectory(System.IO.Path.GetDirectoryName(path)!);
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            socket.Bind(new UnixDomainSocketEndPoint(path));
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            socket.Listen(8);
        }
        catch
        {
            socket.Dispose();
            throw;
        }

        return new LocalControlServer(socket, path, verifier ?? new PeerCredentialVerifier());
    }

    public void Start(IManagerControl control)
    {
        _control = control;
        _loop = Task.Run(AcceptLoopAsync);
    }

    private async Task AcceptLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            Socket connection;
            try
            {
                connection = await _listener.AcceptAsync(_stop.Token);
            }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                if (_stop.IsCancellationRequested || e is ObjectDisposedException)
                {
                    return;
                }

                continue;
            }

            _ = Task.Run(() => HandleAsync(connection));
        }
    }

    private async Task HandleAsync(Socket connection)
    {
        using (connection)
        {
            try
            {
                if (!_verifier.IsOwner(connection))
                {
                    return;
                }

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                var line = await ReadLineAsync(connection, timeout.Token);
                var response = line is null
                    ? LocalControlResponse.Failure("invalid-request", "The request is missing or larger than 16 KiB.")
                    : await DispatchAsync(line);
                var json = JsonSerializer.Serialize(response, LocalControlProtocol.Json) + "\n";
                await connection.SendAsync(Encoding.UTF8.GetBytes(json), _stop.Token);
                connection.Shutdown(SocketShutdown.Both);
            }
            catch (Exception e) when (e is SocketException or OperationCanceledException or IOException or ObjectDisposedException)
            {
            }
        }
    }

    private static async Task<byte[]?> ReadLineAsync(Socket connection, CancellationToken token)
    {
        var data = new List<byte>();
        var buffer = new byte[2048];
        while (true)
        {
            var read = await connection.ReceiveAsync(buffer, token);
            if (read == 0)
            {
                return data.Count == 0 ? null : data.ToArray();
            }

            for (var i = 0; i < read; i++)
            {
                if (buffer[i] == (byte)'\n')
                {
                    return data.ToArray();
                }

                data.Add(buffer[i]);
                if (data.Count > LocalControlProtocol.MaxRequestBytes)
                {
                    return null;
                }
            }
        }
    }

    private async Task<LocalControlResponse> DispatchAsync(byte[] line)
    {
        JsonObject request;
        try
        {
            request = JsonNode.Parse(line)?.AsObject() ?? throw new JsonException();
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException)
        {
            return LocalControlResponse.Failure("invalid-request", "The request is not a JSON object.");
        }

        if (request["protocolVersion"] is not JsonValue version || !version.TryGetValue<int>(out var v))
        {
            return LocalControlResponse.Failure("invalid-request", "protocolVersion is required.");
        }

        if (v != LocalControlProtocol.Version)
        {
            return LocalControlResponse.Failure("unsupported-protocol-version", "Only protocol version 1 is supported.");
        }

        if (request["command"] is not JsonValue cmd || !cmd.TryGetValue<string>(out var command))
        {
            return LocalControlResponse.Failure("invalid-request", "command is required.");
        }

        var args = request["arguments"] as JsonObject ?? [];
        try
        {
            return await RunAsync(command, args);
        }
        catch (Exception e) when (e is InvalidOperationException or FormatException or JsonException or InvalidCastException)
        {
            return LocalControlResponse.Failure("invalid-request", "The arguments are invalid.");
        }
    }

    private async Task<LocalControlResponse> RunAsync(string command, JsonObject args)
    {
        var control = _control!;
        switch (command)
        {
            case "status":
                return LocalControlResponse.Success(StatusReport(control));
            case "show-status":
                control.RequestShowStatus();
                return Accepted(control, command);
            case "register":
                return await RegisterAsync(control, args);
            case "pause":
                return await PauseAsync(control, args);
            case "resume":
                return Accepted(control, command, await control.ResumeAsync());
            case "exit":
                return Accepted(control, command, await control.ExitAsync());
            case "unregister":
                var discard = args["discardLocal"]?.GetValue<bool>() ?? false;
                return Accepted(control, command, await (discard ? control.DiscardLocalRegistrationAsync() : control.UnregisterAsync()));
            case "concurrency":
                return await ConcurrencyAsync(control, args);
            case "autostart":
                if (args["enabled"] is { } enabled)
                {
                    var set = await control.SetAutostartAsync(enabled.GetValue<bool>());
                    if (!set.Succeeded)
                    {
                        return Refused(set);
                    }
                }

                return LocalControlResponse.Success(new JsonObject
                {
                    ["kind"] = "autostart",
                    ["enabled"] = control.GetStatus().AutostartEnabled ?? false,
                });
            default:
                return LocalControlResponse.Failure("invalid-request", "Unknown command.");
        }
    }

    private static async Task<LocalControlResponse> RegisterAsync(IManagerControl control, JsonObject args)
    {
        var endpoint = args["platformEndpoint"]?.GetValue<string>();
        if (string.IsNullOrEmpty(endpoint))
        {
            return LocalControlResponse.Failure("invalid-request", "platformEndpoint is required.");
        }

        var result = await control.RegisterAsync(new RegisterRequest(
            endpoint, args["stampId"]?.GetValue<string>(), args["hostLabel"]?.GetValue<string>()));
        if (!result.Succeeded)
        {
            return Refused(new ControlResult(false, result.Code));
        }

        return LocalControlResponse.Success(new JsonObject
        {
            ["kind"] = "register-started",
            ["verificationUri"] = result.VerificationUri,
            ["pairingCode"] = result.PairingCode,
            ["deviceFingerprint"] = result.DeviceFingerprint,
            ["expiresAt"] = (result.ExpiresAt ?? DateTimeOffset.UtcNow).ToString("O"),
            ["stampId"] = control.GetStatus().StampId ?? string.Empty,
        });
    }

    private static async Task<LocalControlResponse> ConcurrencyAsync(IManagerControl control, JsonObject args)
    {
        if (args["hostLocal"] is { } value)
        {
            if (value is not JsonValue number || !number.TryGetValue<int>(out var limit) || limit < 1)
            {
                return LocalControlResponse.Failure("invalid-concurrency", "hostLocal must be an integer of at least 1.");
            }

            var set = await control.SetHostLocalConcurrencyAsync(limit);
            if (!set.Succeeded)
            {
                return Refused(set);
            }
        }

        return LocalControlResponse.Success(ConcurrencyReport(control.GetConcurrency()));
    }

    public static JsonObject ConcurrencyReport(Work.ConcurrencySnapshot snapshot) => new()
    {
        ["kind"] = "concurrency",
        ["hostLocal"] = snapshot.HostLocal,
        ["stampMaximum"] = snapshot.StampMaximum,
        ["runtimeMaximum"] = snapshot.RuntimeMaximum,
        ["effective"] = snapshot.Effective,
    };

    private static async Task<LocalControlResponse> PauseAsync(IManagerControl control, JsonObject args)
    {
        var minutes = args["durationMinutes"]?.GetValue<int>();
        var until = args["until"]?.GetValue<string>();
        if (minutes is not null && until is not null)
        {
            return LocalControlResponse.Failure("invalid-request", "Use either durationMinutes or until.");
        }

        var result = minutes is { } m
            ? await control.PauseAsync(TimeSpan.FromMinutes(m))
            : until is not null
                ? await control.PauseUntilAsync(DateTimeOffset.Parse(until, System.Globalization.CultureInfo.InvariantCulture))
                : await control.PauseAsync();
        return Accepted(control, "pause", result);
    }

    private static LocalControlResponse Accepted(IManagerControl control, string command, ControlResult? result = null)
    {
        if (result is { Succeeded: false })
        {
            return Refused(result);
        }

        var status = control.GetStatus();
        return LocalControlResponse.Success(new JsonObject
        {
            ["kind"] = "operation-accepted",
            ["command"] = command,
            ["registrationState"] = status.RegistrationState,
            ["operatingState"] = status.OperatingState,
        });
    }

    private static LocalControlResponse Refused(ControlResult result)
    {
        var code = result.Code is { } c && SchemaErrorCodes.Contains(c) ? c : "invalid-operating-state";
        return LocalControlResponse.Failure(code, result.Notice ?? $"The Manager refused the command ({result.Code}).");
    }

    public static JsonObject StatusReport(IManagerControl control)
    {
        var node = JsonSerializer.SerializeToNode(control.GetStatus(), LocalControlProtocol.Json)!.AsObject();
        node["kind"] = "status";
        return node;
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Dispose();
        try
        {
            _loop?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
        }

        if (File.Exists(Path))
        {
            File.Delete(Path);
        }

        _stop.Dispose();
    }
}
