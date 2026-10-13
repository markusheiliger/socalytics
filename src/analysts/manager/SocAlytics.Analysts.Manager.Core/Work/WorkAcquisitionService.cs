using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SocAlytics.Analysts.Manager.Hosting;
using SocAlytics.Analysts.Manager.Operating;
using SocAlytics.Analysts.Manager.Platform;
using SocAlytics.Analysts.Manager.Queue;
using SocAlytics.Analysts.Manager.Runtime;

namespace SocAlytics.Analysts.Manager.Work;

/// <summary>
/// Pulls one delivery at a time while <see cref="AdmissionGate"/> admits and a slot below the effective limit is free,
/// round-robin over the consumers of the capabilities the validated runtime offers, and hands each to an <see cref="AttemptRunner"/>.
/// </summary>
public sealed class WorkAcquisitionService(
    AdmissionGate admission,
    AttemptRegistry registry,
    WorkSessionProvider sessions,
    QueueCredentialProvider credentials,
    IQueueConnection queue,
    PlatformClient platform,
    RuntimeAdapterCatalog catalog,
    PreflightScheduler preflight,
    IOptions<RuntimeOptions> runtimeOptions,
    TimeProvider time,
    ILoggerFactory loggers) : BackgroundService
{
    public static readonly TimeSpan FetchWait = TimeSpan.FromSeconds(1);

    private static readonly TimeSpan IdleDelay = TimeSpan.FromMilliseconds(250);

    private readonly ILogger _logger = loggers.CreateLogger<WorkAcquisitionService>();
    private AttemptRunner? _runner;
    private IContainerRuntimeAdapter? _runnerAdapter;
    private int _next;

    /// <summary>The number of attempts that may run at once: the lower of the stamp maximum and the validated runtime's maximum.</summary>
    public int EffectiveLimit()
    {
        var stamp = credentials.Current?.MaxConcurrentAnalysts ?? 0;
        var runtime = preflight.Latest is { Passed: true } latest ? latest.MaxParallelAttempts : 0;
        return Math.Max(0, Math.Min(stamp, runtime));
    }

    /// <summary>One acquisition step; returns whether a delivery was fetched and started.</summary>
    public async Task<bool> StepAsync(CancellationToken cancellationToken)
    {
        if (admission.TryAdmit() || registry.ActiveCount > 0)
        {
            await sessions.RefreshAsync(cancellationToken);
        }

        if (!admission.TryAdmit() || registry.ActiveCount >= EffectiveLimit())
        {
            return false;
        }

        var adapter = catalog.Find(runtimeOptions.Value.Profile);
        var credential = credentials.Current;
        if (adapter is null || credential is null)
        {
            return false;
        }

        var offered = adapter.Capabilities.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        var consumers = credential.Consumers.Where(c => offered.Contains(c.Capability)).ToList();
        for (var i = 0; i < consumers.Count; i++)
        {
            var capability = consumers[(_next + i) % consumers.Count].Capability;
            IQueueDelivery? delivery;
            try
            {
                delivery = await queue.FetchOneAsync(capability, FetchWait, cancellationToken);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                _logger.LogWarning("Fetching work for capability {Capability} failed: {Error}", capability, e.GetType().Name);
                return false;
            }

            if (delivery is null)
            {
                continue;
            }

            _next = (_next + i + 1) % consumers.Count;
            Start(delivery, adapter, cancellationToken);
            return true;
        }

        return false;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var renewal = credentials.RunAsync(
            () => admission.TryAdmit() ? sessions.Current : null,
            () => catalog.Find(runtimeOptions.Value.Profile)?.Capabilities ?? [],
            stoppingToken);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                bool started;
                try
                {
                    started = await StepAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception e) when (e is HttpRequestException or PlatformException)
                {
                    _logger.LogWarning("Work acquisition step failed: {Error}", e.GetType().Name);
                    started = false;
                }

                if (!started)
                {
                    await Task.Delay(IdleDelay, time, stoppingToken);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await registry.StopAndCleanUpAsync(bound.Token);
            await renewal;
        }
    }

    private void Start(IQueueDelivery delivery, IContainerRuntimeAdapter adapter, CancellationToken stopping)
    {
        if (_runner is null || !ReferenceEquals(adapter, _runnerAdapter))
        {
            _runnerAdapter = adapter;
            _runner = new AttemptRunner(platform, adapter, time, loggers.CreateLogger<AttemptRunner>());
        }

        var runner = _runner;
        _ = registry.Run(
            async (started, token) =>
            {
                var end = await runner.RunAsync(delivery, () => sessions.Current, started, token);
                _logger.LogDebug("Attempt run for capability {Capability} ended: {End}", delivery.Capability, end);
            },
            stopping);
    }
}
