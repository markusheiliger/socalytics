using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SocAlytics.Analysts.Manager.Platform;
using SocAlytics.Analysts.Manager.Queue;
using SocAlytics.Analysts.Manager.Runtime;

namespace SocAlytics.Analysts.Manager.Work;

public enum AttemptRunEnd
{
    /// <summary>The platform accepted or replayed the completion.</summary>
    Completed,

    /// <summary>The claim was obsolete; the delivery was acknowledged and no work ran.</summary>
    ClaimObsolete,

    /// <summary>The claim got no outcome; the delivery stays unacknowledged for redelivery.</summary>
    ClaimDeferred,

    /// <summary>The delivery is unusable or the platform refused the claim; it stays unacknowledged.</summary>
    Rejected,

    /// <summary>The attempt was fenced, lost, or the registration became inactive; nothing was reported.</summary>
    Abandoned,

    /// <summary>Admission ended after the fetch; the delivery was negatively acknowledged without delay and not claimed.</summary>
    NotAdmitted,

    /// <summary>The host cannot meet the pinned resources; the delivery was negatively acknowledged with a delay and not claimed.</summary>
    Unfit,
}

/// <summary>Claims one fetched delivery, runs the attempt through the runtime adapter, keeps the lease alive, and reports once.</summary>
public sealed class AttemptRunner(
    PlatformClient platform,
    IContainerRuntimeAdapter runtime,
    TimeProvider time,
    ILogger<AttemptRunner>? logger = null,
    ExecutedRuntime? executedRuntime = null,
    ResultPublisher? publisher = null,
    UpstreamStager? stager = null,
    SegmentMediaStager? mediaStager = null,
    Registration.MinimumVersionState? minimumVersion = null)
{
    public static readonly TimeSpan ClaimRetryWindow = TimeSpan.FromSeconds(20);

    public const string JobFileName = "job.json";
    public const string AttemptFileName = "attempt.json";
    public const string ScratchFolder = "scratch";

    private static readonly TimeSpan InitialBackoff = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);

    // Attempt directories of this process, which the start-up cleanup of a previous process's leftovers must not touch.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> LiveDirectories = new();

    // Categories a runtime adapter may report as its failure reason; any other reason is an Analyst error.
    private static readonly HashSet<string> RuntimeFailureCategories = ["resource-exhausted", "image-unavailable", "runtime-unavailable"];

    private readonly ILogger _logger = logger ?? NullLogger<AttemptRunner>.Instance;
    private readonly ExecutedRuntime _executedRuntime = executedRuntime ?? Describe(runtime.Profile);

    public async Task<AttemptRunEnd> RunAsync(
        IQueueDelivery delivery, Func<PlatformSession?> sessions, Action<ActiveAttempt>? started, CancellationToken cancellationToken,
        AttemptAdmission? admission = null)
    {
        AnalystJobMessage? job;
        try
        {
            job = JsonSerializer.Deserialize<AnalystJobMessage>(delivery.Data.Span, PlatformJson.Options);
        }
        catch (JsonException)
        {
            job = null;
        }

        if (job is null || !Guid.TryParse(job.LogicalJobId, out var jobId)
            || !Guid.TryParse(job.ReadinessOccurrenceId, out var occurrenceId) || !Guid.TryParse(job.MessageId, out var messageId))
        {
            _logger.LogWarning("Delivery for capability {Capability} is not a usable analyst job and stays unacknowledged", delivery.Capability);
            return AttemptRunEnd.Rejected;
        }

        if (admission is not null)
        {
            if (!admission.IsAdmitted())
            {
                await NegativelyAcknowledgeAsync(delivery, job, null, cancellationToken);
                return AttemptRunEnd.NotAdmitted;
            }

            if (!admission.Capacity.Fits(job.Resources))
            {
                await NegativelyAcknowledgeAsync(delivery, job, admission.UnfitDelay(), cancellationToken);
                return AttemptRunEnd.Unfit;
            }
        }

        var claim = await ClaimAsync(job, jobId, occurrenceId, messageId, sessions, cancellationToken);
        switch (claim.Kind)
        {
            case WorkResultKind.Success:
            case WorkResultKind.Replayed:
                break;
            case WorkResultKind.Obsolete:
                await AcknowledgeAsync(delivery, job, "claim-obsolete", cancellationToken);
                return AttemptRunEnd.ClaimObsolete;
            case WorkResultKind.Transient:
            case WorkResultKind.Inactive:
                _logger.LogWarning(
                    "Claim for Logical Job {LogicalJobId} correlation {CorrelationId} got no outcome ({Kind}); delivery stays unacknowledged",
                    job.LogicalJobId, job.Correlation.CorrelationId, claim.Kind);
                return AttemptRunEnd.ClaimDeferred;
            case WorkResultKind.Refused when claim.IsVersionRefusal:
                minimumVersion?.MarkRefused(claim.MinimumManagerVersion);
                _logger.LogWarning(
                    "Claim for Logical Job {LogicalJobId} correlation {CorrelationId} was refused: the Manager version is below the stamp minimum {Minimum}",
                    job.LogicalJobId, job.Correlation.CorrelationId, claim.MinimumManagerVersion);
                await NegativelyAcknowledgeAsync(delivery, job, null, cancellationToken);
                return AttemptRunEnd.NotAdmitted;
            default:
                _logger.LogWarning(
                    "Claim for Logical Job {LogicalJobId} correlation {CorrelationId} was refused ({Code}); delivery stays unacknowledged",
                    job.LogicalJobId, job.Correlation.CorrelationId, claim.Code ?? claim.Kind.ToString());
                return AttemptRunEnd.Rejected;
        }

        var attempt = new ActiveAttempt(job, claim.Value!, time.GetUtcNow(), delivery.Data.ToArray());
        _logger.LogInformation(
            "Claimed Logical Job {LogicalJobId} attempt {AttemptId} number {AttemptNumber} correlation {CorrelationId} (replayed: {Replayed})",
            attempt.LogicalJobId, attempt.AttemptId, attempt.AttemptNumber, attempt.CorrelationId, claim.Kind == WorkResultKind.Replayed);
        await AcknowledgeAsync(delivery, job, "claim", cancellationToken);
        started?.Invoke(attempt);
        return await ExecuteAsync(attempt, sessions, cancellationToken);
    }

    private async Task<WorkResult<ClaimGranted>> ClaimAsync(
        AnalystJobMessage job, Guid jobId, Guid occurrenceId, Guid messageId, Func<PlatformSession?> sessions,
        CancellationToken cancellationToken)
    {
        var retryKey = $"{messageId}.{occurrenceId}";
        var deadline = time.GetUtcNow() + ClaimRetryWindow;
        var backoff = InitialBackoff;
        while (true)
        {
            var session = sessions();
            if (session is null)
            {
                return new(WorkResultKind.Inactive);
            }

            var result = await platform.ClaimAsync(session, jobId, occurrenceId, messageId, retryKey, cancellationToken);
            if (result.Kind != WorkResultKind.Transient || time.GetUtcNow() + backoff >= deadline)
            {
                return result;
            }

            _logger.LogWarning(
                "Claim for Logical Job {LogicalJobId} correlation {CorrelationId} failed transiently; retrying with the same key",
                job.LogicalJobId, job.Correlation.CorrelationId);
            await Task.Delay(backoff, time, cancellationToken);
            backoff = Min(backoff * 2, MaxBackoff);
        }
    }

    private async Task NegativelyAcknowledgeAsync(IQueueDelivery delivery, AnalystJobMessage job, TimeSpan? delay, CancellationToken cancellationToken)
    {
        try
        {
            await (delay is { } d ? delivery.NakAsync(d, cancellationToken) : delivery.NakAsync(cancellationToken));
            _logger.LogInformation(
                "Negatively acknowledged unclaimed delivery of Logical Job {LogicalJobId} correlation {CorrelationId} ({Reason})",
                job.LogicalJobId, job.Correlation.CorrelationId, delay is null ? "admission ended" : "resources do not fit");
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // An unacknowledged delivery is redelivered after the acknowledgement wait.
            _logger.LogWarning(
                "Negatively acknowledging the delivery of Logical Job {LogicalJobId} correlation {CorrelationId} failed: {Error}",
                job.LogicalJobId, job.Correlation.CorrelationId, e.GetType().Name);
        }
    }

    private async Task AcknowledgeAsync(IQueueDelivery delivery, AnalystJobMessage job, string reason, CancellationToken cancellationToken)
    {
        try
        {
            await delivery.AckAsync(cancellationToken);
            _logger.LogInformation(
                "Acknowledged delivery of Logical Job {LogicalJobId} correlation {CorrelationId} after {Reason} outcome",
                job.LogicalJobId, job.Correlation.CorrelationId, reason);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // An unacknowledged delivery is redelivered; the platform then answers the claim with the same attempt or obsolete.
            _logger.LogWarning(
                "Acknowledging the delivery of Logical Job {LogicalJobId} correlation {CorrelationId} failed: {Error}",
                job.LogicalJobId, job.Correlation.CorrelationId, e.GetType().Name);
        }
    }

    private async Task<AttemptRunEnd> ExecuteAsync(ActiveAttempt attempt, Func<PlatformSession?> sessions, CancellationToken cancellationToken)
    {
        using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var heartbeat = new HeartbeatLoop(platform, time, _logger).RunAsync(attempt, sessions, heartbeatCts.Token);
        IAttemptExecution? execution = null;
        var cleanedUp = false;
        try
        {
            AttemptOutcome? outcome = null;
            CompletionFailure? failure = null;
            try
            {
                string? inputsDirectory = null;
                string? mediaDirectory = null;
                var attemptDirectory = UpstreamStager.DirectoryFor(attempt.AttemptId);
                LiveDirectories[attemptDirectory] = 0;
                PrepareAttemptDirectory(attempt, attemptDirectory);
                if (stager is not null && UpstreamStager.HasUpstream(attempt.Job))
                {
                    var staged = await stager.StageAsync(attempt, attemptDirectory, sessions, cancellationToken);
                    if (staged.End == StagingEnd.Obsolete)
                    {
                        heartbeatCts.Cancel();
                        return AttemptRunEnd.Abandoned;
                    }

                    if (staged.End == StagingEnd.Failed)
                    {
                        throw new StagingFailedException(staged.FailureCategory!);
                    }

                    inputsDirectory = Path.Combine(attemptDirectory, UpstreamStager.InputsFolder);
                }

                if (mediaStager is not null && SegmentMediaStager.HasMedia(attempt.Job))
                {
                    var staged = await mediaStager.StageAsync(attempt, attemptDirectory, sessions, cancellationToken);
                    if (staged.End == StagingEnd.Obsolete)
                    {
                        heartbeatCts.Cancel();
                        return AttemptRunEnd.Abandoned;
                    }

                    if (staged.End == StagingEnd.Failed)
                    {
                        throw new StagingFailedException(staged.FailureCategory!);
                    }

                    mediaDirectory = Path.Combine(attemptDirectory, SegmentMediaStager.MediaFolder);
                }

                execution = await runtime.StartAttemptAsync(Launch(attempt, inputsDirectory, mediaDirectory), cancellationToken);
                attempt.ExecutionState = AttemptExecutionStates.Running;

                using var timeoutCts = new CancellationTokenSource();
                var timeout = Task.Delay(attempt.ExecutionTimeout, time, timeoutCts.Token);
                var finished = await Task.WhenAny(execution.Outcome, heartbeat, timeout);
                timeoutCts.Cancel();
                if (finished == heartbeat)
                {
                    heartbeatCts.Cancel();
                    if (attempt.StopReason == AttemptStopReason.DrainTimeout)
                    {
                        _logger.LogWarning(
                            "Logical Job {LogicalJobId} attempt {AttemptId} correlation {CorrelationId} is stopped: drain-timeout",
                            attempt.LogicalJobId, attempt.AttemptId, attempt.CorrelationId);
                        failure = new CompletionFailure("cancelled-by-manager");
                    }
                    else
                    {
                        await StopAsync(attempt, execution);
                        _logger.LogWarning(
                            "Abandoning Logical Job {LogicalJobId} attempt {AttemptId} correlation {CorrelationId}: heartbeat ended ({End})",
                            attempt.LogicalJobId, attempt.AttemptId, attempt.CorrelationId, await heartbeat);
                        return AttemptRunEnd.Abandoned;
                    }
                }
                else if (finished == timeout)
                {
                    _logger.LogWarning(
                        "Logical Job {LogicalJobId} attempt {AttemptId} correlation {CorrelationId} exceeded its execution timeout",
                        attempt.LogicalJobId, attempt.AttemptId, attempt.CorrelationId);
                    failure = new CompletionFailure("execution-timeout");
                }
                else
                {
                    outcome = await ReadOutcomeAsync(execution);
                    failure = outcome.Succeeded ? null : new CompletionFailure(RuntimeFailureCategories.Contains(outcome.FailureReason ?? string.Empty)
                        ? outcome.FailureReason!
                        : "analyst-error");
                    if (outcome.Succeeded && publisher is not null)
                    {
                        var published = await publisher.PublishAsync(
                            attempt, ResultPublisher.DirectoryFor(attempt.AttemptId), sessions, cancellationToken);
                        switch (published.End)
                        {
                            case ResultPublishEnd.Published:
                                outcome = AttemptOutcome.Success(published.ManifestReference!, published.ManifestChecksum!);
                                break;
                            case ResultPublishEnd.Stopped:
                                heartbeatCts.Cancel();
                                _logger.LogWarning(
                                    "Abandoning Logical Job {LogicalJobId} attempt {AttemptId} correlation {CorrelationId}: result grants were refused",
                                    attempt.LogicalJobId, attempt.AttemptId, attempt.CorrelationId);
                                return AttemptRunEnd.Abandoned;
                            case ResultPublishEnd.Invalid:
                                outcome = AttemptOutcome.Failure("result-manifest-schema-invalid");
                                failure = new CompletionFailure("analyst-error");
                                break;
                            default:
                                outcome = AttemptOutcome.Failure("result-storage-unavailable");
                                failure = new CompletionFailure("runtime-unavailable");
                                break;
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                failure = new CompletionFailure("cancelled-by-manager");
            }
            catch (StagingFailedException e)
            {
                failure = new CompletionFailure(e.Category);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                _logger.LogWarning(
                    "Logical Job {LogicalJobId} attempt {AttemptId} correlation {CorrelationId} could not run: {Error}",
                    attempt.LogicalJobId, attempt.AttemptId, attempt.CorrelationId, e.GetType().Name);
                failure = new CompletionFailure("runtime-unavailable");
            }

            attempt.ExecutionState = AttemptExecutionStates.Finalizing;
            if (execution is not null && failure is not null)
            {
                await StopAsync(attempt, execution);
                await CleanUpAsync(attempt, execution);
                cleanedUp = true;
            }

            return await ReportAsync(attempt, outcome, failure, sessions, cancellationToken);
        }
        finally
        {
            heartbeatCts.Cancel();
            await heartbeat;
            if (execution is not null && !cleanedUp)
            {
                await CleanUpAsync(attempt, execution);
            }

            DeleteAttemptDirectory(attempt);
            LiveDirectories.TryRemove(UpstreamStager.DirectoryFor(attempt.AttemptId), out _);
        }
    }

    /// <summary>Deletes every attempt directory, with its staged inputs, left by a previous Manager process.</summary>
    public static void DeleteLeftoverAttemptDirectories(ILogger? logger = null)
    {
        var root = UpstreamStager.AttemptsRoot;
        if (!Directory.Exists(root))
        {
            return;
        }

        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            if (LiveDirectories.ContainsKey(directory))
            {
                continue;
            }

            try
            {
                Directory.Delete(directory, true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                logger?.LogWarning("Removing a leftover attempt directory failed: {Error}", e.GetType().Name);
            }
        }
    }

    private void DeleteAttemptDirectory(ActiveAttempt attempt)
    {
        try
        {
            Directory.Delete(UpstreamStager.DirectoryFor(attempt.AttemptId), true);
        }
        catch (DirectoryNotFoundException)
        {
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("Removing the attempt directory of attempt {AttemptId} failed: {Error}", attempt.AttemptId, e.GetType().Name);
        }
    }

    private static void PrepareAttemptDirectory(ActiveAttempt attempt, string attemptDirectory)
    {
        Directory.CreateDirectory(attemptDirectory);
        Directory.CreateDirectory(Path.Combine(attemptDirectory, ResultPublisher.ResultFolder));
        Directory.CreateDirectory(Path.Combine(attemptDirectory, ScratchFolder));
        File.WriteAllBytes(Path.Combine(attemptDirectory, JobFileName), attempt.JobBytes);
        File.WriteAllBytes(
            Path.Combine(attemptDirectory, AttemptFileName),
            JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
            {
                ["contractVersion"] = "1.0.0",
                ["attemptId"] = attempt.AttemptId.ToString(),
                ["attemptNumber"] = attempt.AttemptNumber,
            }));
    }

    private static async Task<AttemptOutcome> ReadOutcomeAsync(IAttemptExecution execution)
    {
        try
        {
            return await execution.Outcome;
        }
        catch (Exception)
        {
            return AttemptOutcome.Failure("analyst lost");
        }
    }

    private async Task StopAsync(ActiveAttempt attempt, IAttemptExecution execution)
    {
        try
        {
            await execution.StopAsync(CancellationToken.None);
        }
        catch (Exception e)
        {
            _logger.LogWarning(
                "Stopping Logical Job {LogicalJobId} attempt {AttemptId} correlation {CorrelationId} failed: {Error}",
                attempt.LogicalJobId, attempt.AttemptId, attempt.CorrelationId, e.GetType().Name);
        }
    }

    private async Task CleanUpAsync(ActiveAttempt attempt, IAttemptExecution execution)
    {
        try
        {
            await execution.CleanUpAsync(CancellationToken.None);
            _logger.LogInformation(
                "Cleaned up Logical Job {LogicalJobId} attempt {AttemptId} correlation {CorrelationId}",
                attempt.LogicalJobId, attempt.AttemptId, attempt.CorrelationId);
        }
        catch (Exception e)
        {
            _logger.LogWarning(
                "Cleaning up Logical Job {LogicalJobId} attempt {AttemptId} correlation {CorrelationId} failed: {Error}",
                attempt.LogicalJobId, attempt.AttemptId, attempt.CorrelationId, e.GetType().Name);
        }
    }

    private async Task<AttemptRunEnd> ReportAsync(
        ActiveAttempt attempt, AttemptOutcome? outcome, CompletionFailure? failure, Func<PlatformSession?> sessions,
        CancellationToken cancellationToken)
    {
        if (attempt.StopReason == AttemptStopReason.Revoked)
        {
            return AttemptRunEnd.Abandoned;
        }

        var job = attempt.Job;
        var succeeded = outcome is { Succeeded: true };
        var completion = new AttemptCompletion(
            "1.0.0", job.TeamId, job.MatchId, job.RunId, job.NodeId, job.LogicalJobId.ToString(), attempt.AttemptId.ToString(),
            attempt.AttemptNumber, attempt.FencingToken, succeeded ? "succeeded" : "failed",
            succeeded ? new CompletionResult(outcome!.ResultReference!, outcome.ResultChecksum!) : null,
            succeeded ? null : failure ?? new CompletionFailure("analyst-error"),
            new CompletionCapability(job.Capability.Id, job.Capability.Version), job.SchemaVersion, _executedRuntime,
            job.Pinned.ImageDigest, job.Pinned.ModelDigest,
            (job.Inputs ?? []).SelectMany(i => i.AcceptedResults ?? []).Select(r => r.AcceptedResultId).ToList(),
            time.GetUtcNow(), job.Correlation);

        // A cancelled attempt (drain timeout, exit) gets one bounded best-effort report; otherwise lost responses are replayed.
        var bestEffort = failure is { Category: "cancelled-by-manager" };
        var backoff = InitialBackoff;
        while (true)
        {
            var session = sessions();
            if (session is null)
            {
                return AttemptRunEnd.Abandoned;
            }

            using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            WorkResult<CompletionOutcome> result;
            try
            {
                result = await platform.CompleteAsync(
                    session, attempt.AttemptId, completion, attempt.CompletionIdempotencyKey,
                    bestEffort ? bound.Token : cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return AttemptRunEnd.Abandoned;
            }

            switch (result.Kind)
            {
                case WorkResultKind.Success:
                case WorkResultKind.Replayed:
                    if (result.Value is { Outcome: "failed" } && completion.Outcome == "succeeded")
                    {
                        _logger.LogWarning(
                            "Result of Logical Job {LogicalJobId} attempt {AttemptId} correlation {CorrelationId} was rejected ({Reason}); the attempt ends without a retry",
                            attempt.LogicalJobId, attempt.AttemptId, attempt.CorrelationId, result.Value.Reason);
                        return AttemptRunEnd.Completed;
                    }

                    _logger.LogInformation(
                        "Completed Logical Job {LogicalJobId} attempt {AttemptId} correlation {CorrelationId} as {Outcome} (replayed: {Replayed})",
                        attempt.LogicalJobId, attempt.AttemptId, attempt.CorrelationId, completion.Outcome, result.Kind == WorkResultKind.Replayed);
                    return AttemptRunEnd.Completed;
                case WorkResultKind.Obsolete:
                    _logger.LogWarning(
                        "Attempt {AttemptId} of Logical Job {LogicalJobId} correlation {CorrelationId} was fenced: completion rejected as obsolete",
                        attempt.AttemptId, attempt.LogicalJobId, attempt.CorrelationId);
                    return AttemptRunEnd.Abandoned;
                case WorkResultKind.Transient when !bestEffort:
                    _logger.LogWarning(
                        "Completion of Logical Job {LogicalJobId} attempt {AttemptId} correlation {CorrelationId} got no response; replaying with the same key",
                        attempt.LogicalJobId, attempt.AttemptId, attempt.CorrelationId);
                    try
                    {
                        await Task.Delay(backoff, time, cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        return AttemptRunEnd.Abandoned;
                    }

                    backoff = Min(backoff * 2, MaxBackoff);
                    continue;
                default:
                    _logger.LogWarning(
                        "Completion of Logical Job {LogicalJobId} attempt {AttemptId} correlation {CorrelationId} was not recorded ({Kind} {Code})",
                        attempt.LogicalJobId, attempt.AttemptId, attempt.CorrelationId, result.Kind, result.Code);
                    return AttemptRunEnd.Abandoned;
            }
        }
    }

    private sealed class StagingFailedException(string category) : Exception(category)
    {
        public string Category { get; } = category;
    }

    private AttemptLaunch Launch(ActiveAttempt attempt, string? inputsDirectory, string? mediaDirectory)
    {
        var job = attempt.Job;
        var attemptDirectory = UpstreamStager.DirectoryFor(attempt.AttemptId);
        return new AttemptLaunch(
            attempt.AttemptId.ToString(), job.LogicalJobId, job.Capability.Id, $"{job.Capability.Id}:{job.Capability.Version}",
            job.ExecutionScope, job.Pinned.AnalystProfileDigest, job.Pinned.ImageDigest, job.Pinned.ModelDigest ?? string.Empty,
            new AttemptResources(job.Resources.CpuMillicores / 1000.0, job.Resources.MemoryMebibytes * 1024 * 1024, []),
            job, attempt.AttemptNumber, publisher is null ? null : ResultPublisher.DirectoryFor(attempt.AttemptId), inputsDirectory,
            Path.Combine(attemptDirectory, JobFileName), Path.Combine(attemptDirectory, ScratchFolder),
            Path.Combine(attemptDirectory, AttemptFileName), mediaDirectory);
    }

    private static ExecutedRuntime Describe(string profile) => new(
        profile, "1.0",
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "windows" : RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "macos" : "linux",
        RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "x86-64", []);

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;
}
