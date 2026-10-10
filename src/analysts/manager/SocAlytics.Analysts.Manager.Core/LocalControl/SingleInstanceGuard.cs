using System.Net.Sockets;
using System.Text.Json.Nodes;

namespace SocAlytics.Analysts.Manager.LocalControl;

public static class SingleInstanceGuard
{
    /// <summary>Returns the bound server, or null when a live instance answered and was asked to show its status.</summary>
    public static async Task<LocalControlServer?> AcquireAsync(string path, PeerCredentialVerifier? verifier = null)
    {
        try
        {
            return LocalControlServer.Bind(path, verifier);
        }
        catch (SocketException)
        {
        }

        try
        {
            var response = await LocalControlClient.SendAsync(
                path, new LocalControlRequest(LocalControlProtocol.Version, "show-status", new JsonObject()), TimeSpan.FromSeconds(3));
            if (response.Ok)
            {
                return null;
            }
        }
        catch (LocalControlUnavailableException)
        {
        }

        // Unanswered socket: stale, replace it.
        File.Delete(path);
        return LocalControlServer.Bind(path, verifier);
    }
}
