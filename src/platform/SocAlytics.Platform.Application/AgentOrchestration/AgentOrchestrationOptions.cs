using SocAlytics.Platform.Domain.AgentOrchestration;

namespace SocAlytics.Platform.Application.AgentOrchestration;

// Defaults are the explicit non-production values of research R5 and R7; production values are deferred.
public sealed class AgentOrchestrationOptions
{
    public const string SectionName = "AgentOrchestration";

    public ExecutionOptions Execution { get; set; } = new();

    public WorkloadOptions Workloads { get; set; } = new();

    public MonitorOptions Monitor { get; set; } = new();

    public static bool IsValid(AgentOrchestrationOptions options)
    {
        var execution = options.Execution;
        var workloads = options.Workloads;
        var monitor = options.Monitor;

        if (execution is null
            || workloads?.Clients is null
            || monitor is null)
        {
            return false;
        }

        if (execution.MaxAttempts <= 0
            || execution.LeaseSeconds <= 0
            || execution.MaxAttemptSeconds <= 0
            || execution.HistoryTurns <= 0
            || execution.MaxQuestionCharacters <= 0
            || execution.MaxAdviceCharacters <= 0
            || execution.MaxCheckpointNoteCharacters <= 0
            || execution.MaxUnfinishedInvocationsPerOwner <= 0
            || execution.SuggestedPollingSeconds <= 0)
        {
            return false;
        }

        if (workloads.TokenLifetime <= TimeSpan.Zero)
        {
            return false;
        }

        if (workloads.Clients.Any(c => c is null || string.IsNullOrWhiteSpace(c.ClientId) || (c.Enabled && string.IsNullOrWhiteSpace(c.PublicJwk)))
            || workloads.Clients.Select(c => c.ClientId).Distinct(StringComparer.Ordinal).Count() != workloads.Clients.Count)
        {
            return false;
        }

        return monitor.MonitorInterval > TimeSpan.Zero
            && monitor.ReconciliationInterval > TimeSpan.Zero
            && monitor.RepublishBound > TimeSpan.Zero;
    }

    public sealed class ExecutionOptions
    {
        public int MaxAttempts { get; set; } = 3;

        public int LeaseSeconds { get; set; } = 30;

        public int MaxAttemptSeconds { get; set; } = 300;

        public int HistoryTurns { get; set; } = 10;

        public int MaxQuestionCharacters { get; set; } = 4_000;

        public int MaxAdviceCharacters { get; set; } = 16_000;

        public int MaxCheckpointNoteCharacters { get; set; } = 1_000;

        public int MaxUnfinishedInvocationsPerOwner { get; set; } = 5;

        public int SuggestedPollingSeconds { get; set; } = 2;

        public ExecutionPolicy ToPolicy() => new(
            MaxAttempts,
            LeaseSeconds,
            MaxAttemptSeconds,
            HistoryTurns,
            MaxQuestionCharacters,
            MaxAdviceCharacters,
            MaxCheckpointNoteCharacters,
            MaxUnfinishedInvocationsPerOwner,
            SuggestedPollingSeconds);
    }

    public sealed class WorkloadOptions
    {
        public TimeSpan TokenLifetime { get; set; } = TimeSpan.FromMinutes(5);

        public List<WorkloadClientOptions> Clients { get; set; } = [];
    }

    public sealed class WorkloadClientOptions
    {
        public string ClientId { get; set; } = string.Empty;

        public string PublicJwk { get; set; } = string.Empty;

        public bool Enabled { get; set; }
    }

    public sealed class MonitorOptions
    {
        public TimeSpan MonitorInterval { get; set; } = TimeSpan.FromSeconds(5);

        public TimeSpan ReconciliationInterval { get; set; } = TimeSpan.FromSeconds(30);

        public TimeSpan RepublishBound { get; set; } = TimeSpan.FromSeconds(60);
    }
}
