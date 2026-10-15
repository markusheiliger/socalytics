using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace SocAlytics.Analysts.Manager.Runtime.Docker;

/// <summary>
/// One Analyst container for one Execution Attempt. Failures end the outcome with an attempt-completion category
/// (research.md R8); container output goes only to <c>container.log</c> in the attempt directory, never into an outcome.
/// </summary>
public sealed class DockerAttemptExecution : IAttemptExecution
{
    public const string LogFileName = "container.log";
    public const int MaxLogBytes = 1024 * 1024;
    public const string ManifestName = "manifest.json";

    public const string AnalystError = "analyst-error";
    public const string ResourceExhausted = "resource-exhausted";
    public const string ImageUnavailable = "image-unavailable";
    public const string RuntimeUnavailable = "runtime-unavailable";

    private static readonly TimeSpan StopGrace = TimeSpan.FromSeconds(10);

    private readonly DockerEngineClient _engine;
    private readonly RegistryCredentialProvider _credentials;
    private readonly AttemptLaunch _launch;
    private readonly string _imageReference;
    private readonly string _managerId;
    private readonly string _user;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _stopped = new();
    private readonly TaskCompletionSource<AttemptOutcome> _outcome = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile string? _containerId;

    public DockerAttemptExecution(
        DockerEngineClient engine, RegistryCredentialProvider credentials, AttemptLaunch launch, string imageReference,
        string managerId, string user, ILogger? logger = null)
    {
        _engine = engine;
        _credentials = credentials;
        _launch = launch;
        _imageReference = imageReference;
        _managerId = managerId;
        _user = user;
        _logger = logger ?? NullLogger.Instance;
        _ = RunAsync();
    }

    public Task<AttemptOutcome> Outcome => _outcome.Task;

    /// <summary>True when <paramref name="imageReference"/> names a repository the credential is limited to, on its registry host.</summary>
    public static bool IsInsidePrefix(string imageReference, RegistryPullCredential credential)
    {
        var at = imageReference.IndexOf('@', StringComparison.Ordinal);
        var name = at < 0 ? imageReference : imageReference[..at];
        var slash = name.IndexOf('/', StringComparison.Ordinal);
        if (slash <= 0 || !string.Equals(name[..slash], credential.Registry, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var repository = name[(slash + 1)..];
        return credential.Repositories.Any(r => string.Equals(r, repository, StringComparison.Ordinal));
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _stopped.CancelAsync();
        if (_containerId is { } id)
        {
            await GuardedAsync(() => _engine.StopContainerAsync(id, StopGrace, cancellationToken), "stop");
        }
    }

    public async Task CleanUpAsync(CancellationToken cancellationToken)
    {
        await _stopped.CancelAsync();
        if (_containerId is { } id)
        {
            await GuardedAsync(() => _engine.RemoveContainerAsync(id, true, cancellationToken), "removal");
        }
    }

    private async Task GuardedAsync(Func<Task> action, string what)
    {
        try
        {
            await action();
        }
        catch (Exception e) when (e is DockerEngineException or HttpRequestException or IOException)
        {
            _logger.LogWarning("Container {Operation} of attempt {AttemptId} failed: {Error}", what, _launch.AttemptId, e.GetType().Name);
        }
    }

    private async Task RunAsync()
    {
        try
        {
            _outcome.TrySetResult(await ExecuteAsync(_stopped.Token));
        }
        catch (OperationCanceledException)
        {
            _outcome.TrySetCanceled(_stopped.Token);
        }
        catch (Exception e) when (e is DockerEngineException or HttpRequestException or IOException)
        {
            _logger.LogWarning("The container runtime failed for attempt {AttemptId}: {Error}", _launch.AttemptId, e.GetType().Name);
            _outcome.TrySetResult(AttemptOutcome.Failure(RuntimeUnavailable));
        }
        catch (Exception e)
        {
            _logger.LogWarning("Attempt {AttemptId} could not run: {Error}", _launch.AttemptId, e.GetType().Name);
            _outcome.TrySetResult(AttemptOutcome.Failure(RuntimeUnavailable));
        }
    }

    private async Task<AttemptOutcome> ExecuteAsync(CancellationToken ct)
    {
        var credential = _credentials.Current;
        if (credential is null || !IsInsidePrefix(_imageReference, credential))
        {
            _logger.LogWarning("The image of attempt {AttemptId} is outside the registry prefix or no pull credential is available.", _launch.AttemptId);
            return AttemptOutcome.Failure(ImageUnavailable);
        }

        try
        {
            await _engine.PullImageAsync(_imageReference, credential.AuthToken, ct);
        }
        catch (DockerEngineException e) when (e.Kind is not DockerErrorKind.Unreachable and not DockerErrorKind.EndpointRefused
            and not DockerErrorKind.UnsupportedVersion)
        {
            _logger.LogWarning("Pulling the image of attempt {AttemptId} failed: {Kind}", _launch.AttemptId, e.Kind);
            return AttemptOutcome.Failure(ImageUnavailable);
        }

        var body = DockerLaunchSpec.Build(_launch, _imageReference, _managerId, _user);
        var id = await _engine.CreateContainerAsync(body, null, ct);
        _containerId = id;
        await _engine.StartContainerAsync(id, ct);
        var waited = await _engine.WaitContainerAsync(id, ct);

        // The container has ended; bookkeeping must not fail with the stop token.
        var state = await SafeInspectAsync(id);
        await WriteLogAsync(id);

        if (state?.OomKilled == true)
        {
            return AttemptOutcome.Failure(ResourceExhausted);
        }

        if (waited.StatusCode != 0 || waited.Error is not null)
        {
            return AttemptOutcome.Failure(AnalystError);
        }

        var manifest = _launch.ResultDirectory is { } dir ? Path.Combine(dir, ManifestName) : null;
        if (manifest is null || !File.Exists(manifest))
        {
            return AttemptOutcome.Failure(AnalystError);
        }

        var hash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(manifest, ct))).ToLowerInvariant();
        return AttemptOutcome.Success(manifest, "sha-256:" + hash);
    }

    private async Task<DockerContainerState?> SafeInspectAsync(string id)
    {
        try
        {
            return await _engine.InspectContainerAsync(id, CancellationToken.None);
        }
        catch (Exception e) when (e is DockerEngineException or HttpRequestException or IOException)
        {
            return null;
        }
    }

    private async Task WriteLogAsync(string id)
    {
        if (_launch.ResultDirectory is not { } result || Path.GetDirectoryName(result.TrimEnd(Path.DirectorySeparatorChar)) is not { } attemptDirectory)
        {
            return;
        }

        try
        {
            var logs = await _engine.GetLogsAsync(id, 10_000, CancellationToken.None);
            var bytes = System.Text.Encoding.UTF8.GetBytes(logs.Stdout + logs.Stderr);
            var tail = bytes.Length > MaxLogBytes ? bytes[^MaxLogBytes..] : bytes;
            await File.WriteAllBytesAsync(Path.Combine(attemptDirectory, LogFileName), tail, CancellationToken.None);
        }
        catch (Exception e) when (e is DockerEngineException or HttpRequestException or IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("Keeping the container log of attempt {AttemptId} failed: {Error}", _launch.AttemptId, e.GetType().Name);
        }
    }
}
