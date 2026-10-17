using Microsoft.Extensions.Hosting;

namespace SocAlytics.Analysts.Manager.Runtime.Docker;

public enum DockerEndpointKind
{
    NamedPipe,
    UnixSocket,
}

/// <summary>Outcome of the endpoint check; <see cref="Path"/> is the pipe name or socket path when allowed.</summary>
public sealed record DockerEndpointDecision(
    bool Allowed, DockerEndpointKind? Kind, string? Path, string? Reason, string? Remediation);

/// <summary>The runtime endpoint is a privileged local boundary: only a local pipe (Windows) or, for validation, a Unix socket (Development and Test).</summary>
public static class DockerEndpointPolicy
{
    private const string NamedPipePrefix = "npipe://";
    private const string UnixPrefix = "unix://";
    private const string PipeSegment = "/pipe/";

    public static DockerEndpointDecision Evaluate(string? endpoint, IHostEnvironment? environment, bool? isWindows = null)
    {
        var windows = isWindows ?? OperatingSystem.IsWindows();
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            return Refuse("The runtime endpoint is empty.", $"Set {DockerRuntimeOptions.SectionName}:Endpoint to a local endpoint such as {DockerRuntimeOptions.DefaultEndpoint}.");
        }

        if (endpoint.StartsWith(NamedPipePrefix, StringComparison.OrdinalIgnoreCase))
        {
            if (!windows)
            {
                return Refuse("Named pipe endpoints are only available on Windows.", "Use the Windows named pipe endpoint on Windows hosts.");
            }

            var rest = endpoint[NamedPipePrefix.Length..];
            var slash = rest.IndexOf('/');
            var host = slash < 0 ? rest : rest[..slash];
            var path = slash < 0 ? string.Empty : rest[slash..];
            if (host != "." || !path.StartsWith(PipeSegment, StringComparison.OrdinalIgnoreCase) || path.Length == PipeSegment.Length)
            {
                return Refuse("Only local named pipes are allowed.", $"Use an endpoint of the form {DockerRuntimeOptions.DefaultEndpoint}.");
            }

            return new DockerEndpointDecision(true, DockerEndpointKind.NamedPipe, path[PipeSegment.Length..], null, null);
        }

        if (endpoint.StartsWith(UnixPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var validation = environment is not null && (environment.IsDevelopment() || environment.IsEnvironment("Test"));
            if (!validation)
            {
                return Refuse("Unix socket endpoints are only allowed in Development and Test.", "Use the Docker Desktop named pipe endpoint.");
            }

            var path = endpoint[UnixPrefix.Length..];
            if (!path.StartsWith('/') || path.Length == 1)
            {
                return Refuse("The Unix socket endpoint needs an absolute socket path.", "Use an endpoint of the form unix:///var/run/docker.sock.");
            }

            return new DockerEndpointDecision(true, DockerEndpointKind.UnixSocket, path, null, null);
        }

        return Refuse(
            "Only local runtime endpoints are allowed; remote and network endpoints are refused.",
            $"Set {DockerRuntimeOptions.SectionName}:Endpoint to the local Docker Desktop endpoint {DockerRuntimeOptions.DefaultEndpoint}.");
    }

    private static DockerEndpointDecision Refuse(string reason, string remediation) =>
        new(false, null, null, reason, remediation);
}
