using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SocAlytics.Platform.Application.Analysis;
using SocAlytics.Platform.Application.Registry;

namespace SocAlytics.Platform.Infrastructure.Registry;

internal sealed class WorkflowDefinitionAvailability : IWorkflowDefinitionAvailability, IDisposable
{
    private readonly IWorkflowDefinitionResolver _resolver;
    private readonly IOptionsMonitor<AnalysisNotificationOptions> _options;
    private readonly ILogger<WorkflowDefinitionAvailability> _logger;
    private readonly IDisposable? _subscription;
    private readonly object _gate = new();
    private WorkflowDefinitionStatus? _status;
    private long _checkedAt;
    private volatile bool _stale = true;

    public WorkflowDefinitionAvailability(
        IWorkflowDefinitionResolver resolver,
        IOptionsMonitor<ConfiguredWorkflowOptions> workflowOptions,
        IOptionsMonitor<AnalysisNotificationOptions> options,
        ILogger<WorkflowDefinitionAvailability> logger)
    {
        _resolver = resolver;
        _options = options;
        _logger = logger;
        _subscription = workflowOptions.OnChange(_ => _stale = true);
    }

    public WorkflowDefinitionStatus Current
    {
        get
        {
            lock (_gate)
            {
                var recheck = TimeSpan.FromSeconds(Math.Max(1, _options.CurrentValue.ResolveRecheckSeconds));
                if (_status is null || _stale || Stopwatch.GetElapsedTime(_checkedAt) >= recheck)
                {
                    Refresh();
                }

                return _status!;
            }
        }
    }

    public void Dispose() => _subscription?.Dispose();

    private void Refresh()
    {
        _stale = false;
        WorkflowResolution resolution;
        try
        {
            resolution = _resolver.ResolveDefaultAsync(CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            resolution = WorkflowResolution.Unavailable(WorkflowResolution.RegistryUnavailable);
        }

        _checkedAt = Stopwatch.GetTimestamp();
        var (state, category) = resolution is WorkflowResolution.UnavailableResult u
            ? (WorkflowDefinitionStatus.Unresolvable, u.Category)
            : (WorkflowDefinitionStatus.Resolvable, (string?)null);
        if (_status is not null && _status.State == state && _status.Category == category)
        {
            return;
        }

        _status = new WorkflowDefinitionStatus(state, category, DateTimeOffset.UtcNow);
        if (state == WorkflowDefinitionStatus.Unresolvable)
        {
            _logger.LogWarning("The default analysis workflow definition is unresolvable: {Category}", category);
        }
        else
        {
            _logger.LogWarning("The default analysis workflow definition is resolvable.");
        }
    }
}
