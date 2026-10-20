using Microsoft.Extensions.Options;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;
using SocAlytics.Platform.Application.Registry;
using SocAlytics.Platform.Application.Registry.QueueCredentials;

namespace SocAlytics.Platform.Infrastructure.Messaging.Nats;

internal sealed class NatsTopology(
    NatsConnectionProvider provider,
    IOptions<NatsOptions> options,
    IOptions<SocAlytics.Platform.Application.Analysis.AnalysisNotificationOptions> notifications,
    IOptions<AnalystWorkOptions> work,
    IWorkflowDefinitionResolver? workflows = null)
{
    public const string ConsumerPrefix = "analyst-";

    public const string ReadySubjectPrefix = "analysis.jobs.ready.";

    public async Task EnsureAsync(CancellationToken cancellationToken)
    {
        var o = options.Value;
        if (!o.ProvisionTopology)
        {
            return;
        }

        var js = new NatsJSContext(provider.Connection);
        var window = TimeSpan.FromSeconds(o.DuplicateWindowSeconds);
        await js.CreateOrUpdateStreamAsync(
            new StreamConfig(o.EventsStream, o.EventsSubjects) { DuplicateWindow = window },
            cancellationToken).ConfigureAwait(false);
        await js.CreateOrUpdateStreamAsync(
            new StreamConfig(o.JobsStream, o.JobsSubjects)
            {
                DuplicateWindow = window,
                Retention = StreamConfigRetention.Workqueue,
            },
            cancellationToken).ConfigureAwait(false);
        var consumerName = notifications.Value.ConsumerName;
        await js.CreateOrUpdateConsumerAsync(
            o.EventsStream,
            new ConsumerConfig(consumerName)
            {
                DurableName = consumerName,
                FilterSubject = "matches.recordings-finalized",
                AckPolicy = ConsumerConfigAckPolicy.Explicit,
                MaxDeliver = -1,
            },
            cancellationToken).ConfigureAwait(false);

        await js.CreateOrUpdateStreamAsync(
            new StreamConfig(o.AgentWorkStream, [o.AgentWorkSubject])
            {
                DuplicateWindow = window,
                Retention = StreamConfigRetention.Workqueue,
            },
            cancellationToken).ConfigureAwait(false);
        await js.CreateOrUpdateConsumerAsync(
            o.AgentWorkStream,
            new ConsumerConfig(o.AgentWorkConsumer)
            {
                DurableName = o.AgentWorkConsumer,
                FilterSubject = o.AgentWorkSubject,
                AckPolicy = ConsumerConfigAckPolicy.Explicit,
                AckWait = work.Value.ConsumerAckWait,
                MaxDeliver = -1,
            },
            cancellationToken).ConfigureAwait(false);

        if (workflows is null)
        {
            return;
        }

        var resolution = await workflows.ResolveDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (resolution is not WorkflowResolution.ResolvedResult resolved)
        {
            return;
        }

        var capabilities = resolved.Definition.Nodes
            .Select(static n => n.CapabilityId)
            .Distinct(StringComparer.Ordinal);
        foreach (var capabilityId in capabilities)
        {
            if (!IsValidCapabilityToken(capabilityId))
            {
                throw new InvalidOperationException(
                    "A configured capability id cannot be used in a queue subject or consumer name.");
            }

            var name = ConsumerPrefix + capabilityId;
            await js.CreateOrUpdateConsumerAsync(
                o.JobsStream,
                new ConsumerConfig(name)
                {
                    DurableName = name,
                    FilterSubject = ReadySubjectPrefix + capabilityId,
                    AckPolicy = ConsumerConfigAckPolicy.Explicit,
                    AckWait = work.Value.ConsumerAckWait,
                    MaxDeliver = -1,
                },
                cancellationToken).ConfigureAwait(false);
        }
    }

    private static bool IsValidCapabilityToken(string value) =>
        value.Length > 0 && value.All(static c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
}
