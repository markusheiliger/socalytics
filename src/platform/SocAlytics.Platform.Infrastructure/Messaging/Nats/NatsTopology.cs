using Microsoft.Extensions.Options;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;

namespace SocAlytics.Platform.Infrastructure.Messaging.Nats;

internal sealed class NatsTopology(NatsConnectionProvider provider, IOptions<NatsOptions> options)
{
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
    }
}
