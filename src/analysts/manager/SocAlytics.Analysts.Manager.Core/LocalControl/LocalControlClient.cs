using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SocAlytics.Analysts.Manager.LocalControl;

public sealed class LocalControlUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

public static class LocalControlClient
{
    public static async Task<LocalControlResponse> SendAsync(
        string socketPath, LocalControlRequest request, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), cts.Token);
            var line = JsonSerializer.Serialize(request, LocalControlProtocol.Json) + "\n";
            await socket.SendAsync(Encoding.UTF8.GetBytes(line), cts.Token);

            var received = new MemoryStream();
            var buffer = new byte[4096];
            while (true)
            {
                var read = await socket.ReceiveAsync(buffer, cts.Token);
                if (read == 0)
                {
                    break;
                }

                received.Write(buffer, 0, read);
                if (buffer[read - 1] == (byte)'\n')
                {
                    break;
                }
            }

            if (received.Length == 0)
            {
                throw new LocalControlUnavailableException("The Manager closed the connection without answering.");
            }

            return Parse(received.ToArray());
        }
        catch (Exception e) when (e is SocketException or IOException or OperationCanceledException or UnauthorizedAccessException)
        {
            throw new LocalControlUnavailableException("The Manager is not running or its socket is not accessible.", e);
        }
    }

    private static LocalControlResponse Parse(byte[] bytes)
    {
        try
        {
            var node = JsonNode.Parse(bytes)!.AsObject();
            var error = node["error"] is JsonObject e
                ? new LocalControlError((string)e["code"]!, (string?)e["message"] ?? string.Empty, (string?)e["remediation"])
                : null;
            return new LocalControlResponse((int)node["protocolVersion"]!, (bool)node["ok"]!, node["result"]?.DeepClone(), error);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or NullReferenceException or FormatException)
        {
            throw new LocalControlUnavailableException("The Manager answered with an unreadable response.", e);
        }
    }
}
