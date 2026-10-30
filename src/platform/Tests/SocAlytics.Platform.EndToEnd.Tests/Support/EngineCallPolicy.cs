using System.Net;

namespace SocAlytics.Platform.EndToEnd.Tests.Support;

/// <summary>The named limits and retry values of the Docker engine and registry calls of the end-to-end tests (development values).</summary>
internal static class EngineCallPolicy
{
    /// <summary>Time limit of one push attempt.</summary>
    public static readonly TimeSpan PushLimit = TimeSpan.FromMinutes(15);

    /// <summary>Time limit of one image build attempt.</summary>
    public static readonly TimeSpan BuildLimit = TimeSpan.FromMinutes(20);

    /// <summary>Time limit of one registry manifest lookup or engine image-record lookup.</summary>
    public static readonly TimeSpan RegistryLookupLimit = TimeSpan.FromSeconds(30);

    /// <summary>Attempts of a push or build including the first one.</summary>
    public const int MaxAttempts = 3;

    /// <summary>Bounded delay between two attempts of a push or build.</summary>
    public static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(2);

    /// <summary>Runs <paramref name="call"/> under its own limit per attempt and repeats transient failures at most <see cref="MaxAttempts"/> times.</summary>
    public static async Task<T> ExecuteAsync<T>(
        string operation,
        string limitName,
        TimeSpan limit,
        Func<CancellationToken, Task<T>> call,
        CancellationToken ct,
        TimeProvider? time = null)
    {
        time ??= TimeProvider.System;
        var reasons = new List<string>();
        for (var attempt = 1; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            using var limitSource = new CancellationTokenSource(limit, time);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, limitSource.Token);
            try
            {
                return await call(linked.Token);
            }
            catch (Exception e)
            {
                var outcome = Classify(e, ct, limitSource.IsCancellationRequested ? $"{limitName} of {limit} exceeded" : null);
                if (outcome.Kind != EngineCallOutcomeKind.Transient)
                {
                    throw;
                }

                reasons.Add($"attempt {attempt}: {outcome.Reason}");
                if (attempt == MaxAttempts)
                {
                    throw Failed(operation, $"{MaxAttempts} attempts", reasons);
                }
            }

            await Task.Delay(RetryDelay, time, ct);
        }
    }

    /// <summary>Repeats <paramref name="call"/> while it fails transiently, all within one <paramref name="limit"/>; a service that is not yet answering is no result.</summary>
    public static async Task<T> ExecuteWithinAsync<T>(
        string operation,
        string limitName,
        TimeSpan limit,
        Func<CancellationToken, Task<T>> call,
        CancellationToken ct,
        TimeProvider? time = null)
    {
        time ??= TimeProvider.System;
        var reasons = new List<string>();
        using var limitSource = new CancellationTokenSource(limit, time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, limitSource.Token);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await call(linked.Token);
            }
            catch (Exception e)
            {
                var outcome = Classify(e, ct, limitSource.IsCancellationRequested ? $"{limitName} of {limit} exceeded" : null);
                if (outcome.Kind != EngineCallOutcomeKind.Transient)
                {
                    throw;
                }

                reasons.Add($"attempt {attempt}: {outcome.Reason}");
                if (limitSource.IsCancellationRequested)
                {
                    throw Failed(operation, $"{limitName} of {limit}", reasons);
                }
            }

            try
            {
                await Task.Delay(RetryDelay, time, linked.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw Failed(operation, $"{limitName} of {limit}", reasons);
            }
        }
    }

    /// <summary>Classifies a failed attempt; <paramref name="limitReason"/> is set when the attempt's own limit ended it.</summary>
    internal static EngineCallOutcome Classify(Exception exception, CancellationToken ct, string? limitReason = null)
    {
        if (ct.IsCancellationRequested)
        {
            return new EngineCallOutcome(EngineCallOutcomeKind.Cancelled, null, exception);
        }

        if (exception is OperationCanceledException)
        {
            return Transient(limitReason ?? "the call timed out");
        }

        for (var inner = exception; inner is not null; inner = inner.InnerException)
        {
            switch (inner)
            {
                case HttpRequestException { StatusCode: { } status }:
                    return status is HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout or HttpStatusCode.TooManyRequests
                        ? Transient($"HTTP {(int)status}")
                        : new EngineCallOutcome(EngineCallOutcomeKind.Deterministic, null, exception);
                case HttpRequestException or IOException:
                    return Transient($"{inner.GetType().Name}: {inner.Message}");
            }
        }

        return new EngineCallOutcome(EngineCallOutcomeKind.Deterministic, null, exception);
    }

    private static EngineCallOutcome Transient(string reason) => new(EngineCallOutcomeKind.Transient, reason, null);

    private static EngineCallFailedException Failed(string operation, string bound, List<string> reasons) =>
        new($"{operation} failed; bound reached: {bound}.{Environment.NewLine}{string.Join(Environment.NewLine, reasons)}", reasons);
}

/// <summary>The classification of one failed engine call attempt.</summary>
internal enum EngineCallOutcomeKind
{
    Succeeded,
    Transient,
    Deterministic,
    Cancelled,
}

internal sealed record EngineCallOutcome(EngineCallOutcomeKind Kind, string? Reason, Exception? Exception);

/// <summary>A transient failure that persisted until the bound of attempts or time; lists the reason of every attempt.</summary>
internal sealed class EngineCallFailedException(string message, IReadOnlyList<string> reasons) : Exception(message)
{
    public IReadOnlyList<string> Reasons { get; } = reasons;
}
