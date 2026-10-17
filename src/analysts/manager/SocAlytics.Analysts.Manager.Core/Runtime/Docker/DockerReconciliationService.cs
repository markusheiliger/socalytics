using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SocAlytics.Analysts.Manager.Work;

namespace SocAlytics.Analysts.Manager.Runtime.Docker;

/// <summary>
/// Removes what a previous Manager process or a failed cleanup left behind (research.md R9): containers are listed only with the
/// <c>io.socalytics.manager=&lt;registration id&gt;</c> label filter, so containers of other Managers or users are never touched.
/// Failed removals are recorded for the status and retried by the next run.
/// </summary>
public sealed class DockerReconciliationService
{
    private static readonly TimeSpan StopGrace = TimeSpan.FromSeconds(5);

    private readonly Func<DockerEngineClient?> _engine;
    private readonly Func<string?> _managerId;
    private readonly AttemptRegistry _attempts;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly string _attemptsRoot;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile string? _lastManagerId;
    private volatile ReconciliationState? _state;

    public DockerReconciliationService(
        Func<DockerEngineClient?> engine, Func<string?> managerId, AttemptRegistry attempts, TimeProvider time, ILogger? logger = null,
        string? attemptsRoot = null)
    {
        _attemptsRoot = attemptsRoot ?? UpstreamStager.AttemptsRoot;
        _engine = engine;
        _managerId = managerId;
        _attempts = attempts;
        _time = time;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>The result of the last run; null before the first run.</summary>
    public ReconciliationState? State => _state;

    /// <summary>
    /// Removes every labelled container and attempt directory that is not an active attempt of this process; with
    /// <paramref name="removeAll"/> (revocation) active attempts are not spared either.
    /// </summary>
    public async Task<ReconciliationState?> ReconcileAsync(CancellationToken cancellationToken, bool removeAll = false)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var managerId = _managerId();
            if (!string.IsNullOrEmpty(managerId))
            {
                _lastManagerId = managerId;
            }

            managerId = _lastManagerId;
            var engine = _engine();
            if (engine is null || string.IsNullOrEmpty(managerId))
            {
                return _state;
            }

            var leftovers = new List<string>();
            var removedContainers = await ReconcileContainersAsync(engine, managerId, removeAll, leftovers, cancellationToken);
            var removedDirectories = ReconcileDirectories(removeAll, leftovers);
            _state = new ReconciliationState(_time.GetUtcNow(), removedContainers, removedDirectories, leftovers);
            return _state;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<int> ReconcileContainersAsync(
        DockerEngineClient engine, string managerId, bool removeAll, List<string> leftovers, CancellationToken ct)
    {
        IReadOnlyList<DockerContainerSummary> containers;
        try
        {
            containers = await engine.ListContainersAsync([$"{DockerLaunchSpec.ManagerLabel}={managerId}"], ct);
        }
        catch (Exception e) when (IsRuntimeFailure(e, ct))
        {
            _logger.LogWarning("Listing the containers of this Manager failed: {Error}", e.GetType().Name);
            leftovers.Add("containers could not be listed");
            return 0;
        }

        var active = removeAll ? [] : ActiveAttemptIds();
        var removed = 0;
        foreach (var container in containers)
        {
            if (!container.Labels.TryGetValue(DockerLaunchSpec.ManagerLabel, out var owner) || !string.Equals(owner, managerId, StringComparison.Ordinal))
            {
                continue;
            }

            if (container.Labels.TryGetValue(DockerLaunchSpec.AttemptLabel, out var attempt) && active.Contains(attempt))
            {
                continue;
            }

            try
            {
                await engine.StopContainerAsync(container.Id, StopGrace, ct);
                await engine.RemoveContainerAsync(container.Id, true, ct);
                removed++;
            }
            catch (Exception e) when (IsRuntimeFailure(e, ct))
            {
                _logger.LogWarning("Removing a leftover container failed: {Error}", e.GetType().Name);
                leftovers.Add($"container {Short(container.Id)}");
            }
        }

        return removed;
    }

    private int ReconcileDirectories(bool removeAll, List<string> leftovers)
    {
        var root = _attemptsRoot;
        if (!Directory.Exists(root))
        {
            return 0;
        }

        var active = removeAll
            ? []
            : _attempts.Attempts.Select(a => Path.Combine(root, a.AttemptId.ToString("N"))).ToHashSet(StringComparer.Ordinal);
        var removed = 0;
        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            if (active.Contains(directory))
            {
                continue;
            }

            try
            {
                Directory.Delete(directory, true);
                removed++;
            }
            catch (DirectoryNotFoundException)
            {
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning("Removing a leftover attempt directory failed: {Error}", e.GetType().Name);
                leftovers.Add($"attempt directory {Path.GetFileName(directory)}");
            }
        }

        return removed;
    }

    private HashSet<string> ActiveAttemptIds() =>
        _attempts.Attempts.Select(a => a.AttemptId.ToString()).ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static string Short(string id) => id.Length <= 12 ? id : id[..12];

    private static bool IsRuntimeFailure(Exception e, CancellationToken ct) =>
        e is DockerEngineException or HttpRequestException or IOException or InvalidOperationException or System.Text.Json.JsonException
        || (e is OperationCanceledException && !ct.IsCancellationRequested);
}
