namespace SocAlytics.Platform.Infrastructure.Messaging.Nats;

internal sealed class NatsOptions
{
    public const string SectionName = "Messaging:Nats";

    public bool ProvisionTopology { get; set; }

    public string EventsStream { get; set; } = "SOCALYTICS_EVENTS";

    public string[] EventsSubjects { get; set; } = ["matches.>", "analysis.events.>"];

    public string JobsStream { get; set; } = "SOCALYTICS_ANALYSIS_JOBS";

    public string[] JobsSubjects { get; set; } = ["analysis.jobs.ready.>"];

    public string AgentWorkStream { get; set; } = "AGENT_WORK";

    public string AgentWorkSubject { get; set; } = "agents.invocations.ready";

    public string AgentWorkConsumer { get; set; } = "agent-workload";

    public int DuplicateWindowSeconds { get; set; } = 120;
}
