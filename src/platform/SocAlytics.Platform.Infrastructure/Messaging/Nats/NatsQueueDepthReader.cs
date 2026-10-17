using Microsoft.Extensions.Options;
using NATS.Client.JetStream;
using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.Registry.QueueDepth;

namespace SocAlytics.Platform.Infrastructure.Messaging.Nats;

/// <summary>Reads <c>NumPending</c> (ready, not yet delivered) of the per-capability consumers; delivered-but-unacknowledged jobs are not counted.</summary>
internal sealed class NatsQueueDepthReader(NatsConnectionProvider provider, IOptions<NatsOptions> options) : IQueueDepthReader
{
    private static readonly TimeSpan CapabilityTimeout = TimeSpan.FromSeconds(2);

    public async Task<IReadOnlyDictionary<string, long>> ReadWaitingAsync(
        IReadOnlyCollection<string> capabilityIds, CancellationToken cancellationToken)
    {
        if (!provider.IsConfigured)
        {
            throw new MessageTransportException(TransportFailureCategories.TransportUnavailable);
        }

        var js = new NatsJSContext(provider.Connection);
        var stream = options.Value.JobsStream;
        var result = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var id in capabilityIds)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(CapabilityTimeout);
            try
            {
                var consumer = await js.GetConsumerAsync(stream, NatsTopology.ConsumerPrefix + id, timeout.Token).ConfigureAwait(false);
                result[id] = (long)consumer.Info.NumPending;
            }
            catch (NatsJSApiException ex) when (ex.Error.Code == 404)
            {
                // consumer or stream not provisioned: omitted
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new MessageTransportException(TransportFailureCategories.PublishTimeout);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new MessageTransportException(TransportFailureCategories.TransportUnavailable, ex);
            }
        }

        return result;
    }
}
