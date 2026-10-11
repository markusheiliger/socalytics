using Microsoft.Extensions.Options;
using NATS.Client.Core;
using NATS.Client.JetStream;
using SocAlytics.Platform.Application.Abstractions;

namespace SocAlytics.Platform.Infrastructure.Messaging.Nats;

internal sealed class NatsJetStreamTransport(NatsConnectionProvider provider, NatsTopology topology, IOptions<NatsOptions> options)
    : IMessageTransport
{
    private static readonly TimeSpan PublishTimeout = TimeSpan.FromSeconds(5);
    private int _topologyEnsured;

    public async Task<TransportPublishResult> PublishAsync(TransportMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (!provider.IsConfigured)
        {
            throw new MessageTransportException(TransportFailureCategories.TransportUnavailable);
        }

        provider.EnsureStarted();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(PublishTimeout);
        try
        {
            if (options.Value.ProvisionTopology && Volatile.Read(ref _topologyEnsured) == 0)
            {
                await topology.EnsureAsync(timeout.Token).ConfigureAwait(false);
                Volatile.Write(ref _topologyEnsured, 1);
            }

            var headers = new NatsHeaders { ["Nats-Msg-Id"] = message.MessageId };
            var js = new NatsJSContext(provider.Connection);
            var ack = await js.TryPublishAsync(
                message.Subject,
                message.Payload.ToArray(),
                serializer: NatsRawSerializer<byte[]>.Default,
                headers: headers,
                cancellationToken: timeout.Token).ConfigureAwait(false);

            if (!ack.Success)
            {
                throw new MessageTransportException(TransportFailureCategories.PublishRejected, ack.Error);
            }

            var response = ack.Value;
            if (response.Error is not null)
            {
                throw new MessageTransportException(TransportFailureCategories.PublishRejected);
            }

            return response.Duplicate ? TransportPublishResult.Duplicate : TransportPublishResult.Published;
        }
        catch (MessageTransportException)
        {
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex)
        {
            throw new MessageTransportException(TransportFailureCategories.PublishTimeout, ex);
        }
        catch (Exception ex)
        {
            throw new MessageTransportException(TransportFailureCategories.TransportUnavailable, ex);
        }
    }
}
