using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NATS.Client.Core;
using NATS.Client.JetStream;
using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Application.Analysis;
using SocAlytics.Platform.Application.Analysis.Commands;
using SocAlytics.Platform.Application.Analysis.Stores;
using SocAlytics.Platform.Infrastructure.Messaging.Nats;

namespace SocAlytics.Platform.Infrastructure.Workers;

internal sealed class RecordingsFinalizedConsumerWorker(
    IServiceProvider services,
    NatsConnectionProvider nats,
    NatsTopology topology,
    IOptions<NatsOptions> natsOptions,
    IOptions<AnalysisNotificationOptions> options,
    IOptions<AnalysisWorkerOptions> workerOptions,
    TimeProvider timeProvider,
    ILogger<RecordingsFinalizedConsumerWorker> logger) : BackgroundService
{
    private const string MessageType = "recordings-finalized";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!workerOptions.Value.Enabled)
        {
            return;
        }

        if (!nats.IsConfigured)
        {
            logger.LogInformation("Recordings-finalized consumer not started: the transport is not configured.");
            return;
        }

        try
        {
            await Task.Yield();
            var health = services.GetRequiredService<HealthCheckService>();
            await WorkerLoop.WaitForDatabaseAsync(health, timeProvider, stoppingToken).ConfigureAwait(false);
            var loop = new WorkerLoop(
                "recordings-finalized-consumer",
                timeProvider,
                logger,
                TimeSpan.FromMilliseconds(200),
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(30));
            var ready = false;
            await loop.RunAsync(async ct =>
            {
                if (!nats.IsConnected)
                {
                    ready = false;
                    throw new MessageTransportException(TransportFailureCategories.TransportUnavailable);
                }

                try
                {
                    if (!ready)
                    {
                        await topology.EnsureAsync(ct).ConfigureAwait(false);
                        ready = true;
                    }

                    await FetchOnceAsync(ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not MessageTransportException && ex is not Npgsql.NpgsqlException)
                {
                    ready = false;
                    throw new MessageTransportException(TransportFailureCategories.TransportUnavailable, ex);
                }
            }, stoppingToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
        {
            logger.LogError("Recordings-finalized consumer stopped: {Category}", WorkerLoop.Categorize(ex));
        }
    }

    private async Task FetchOnceAsync(CancellationToken ct)
    {
        var js = new NatsJSContext(nats.Connection);
        var consumer = await js.GetConsumerAsync(natsOptions.Value.EventsStream, options.Value.ConsumerName, ct).ConfigureAwait(false);
        var opts = new NatsJSFetchOpts { MaxMsgs = 10, Expires = TimeSpan.FromSeconds(2) };
        await foreach (var msg in consumer.FetchAsync(opts, NatsRawSerializer<byte[]>.Default, cancellationToken: ct).ConfigureAwait(false))
        {
            await ProcessAsync(msg, ct).ConfigureAwait(false);
        }
    }

    private async Task ProcessAsync(INatsJSMsg<byte[]> msg, CancellationToken ct)
    {
        var nak = TimeSpan.FromSeconds(options.Value.NakDelaySeconds);
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(msg.Data ?? []);
        }
        catch (JsonException)
        {
            await msg.AckTerminateAsync(cancellationToken: ct).ConfigureAwait(false);
            return;
        }

        using (document)
        {
            var root = document.RootElement;
            var eventId = TryGuid(root, "eventId");
            await using var scope = services.CreateAsyncScope();
            var validator = scope.ServiceProvider.GetRequiredService<IContractValidator>();
            if (!validator.Validate(ContractSchemaIds.RecordingsFinalizedV1, root).IsValid)
            {
                if (eventId is null)
                {
                    await msg.AckTerminateAsync(cancellationToken: ct).ConfigureAwait(false);
                    return;
                }

                try
                {
                    await RecordSchemaInvalidAsync(scope.ServiceProvider, eventId.Value, TryGuid(root, "recordingSetVersionId"), ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning("Recordings-finalized rejection failed: {Category}", WorkerLoop.Categorize(ex));
                    await msg.NakAsync(nak, cancellationToken: ct).ConfigureAwait(false);
                    return;
                }

                await msg.AckAsync(cancellationToken: ct).ConfigureAwait(false);
                return;
            }

            var notification = new RecordingsFinalizedNotification(
                eventId!.Value,
                root.GetProperty("contractVersion").GetString()!,
                DateTimeOffset.Parse(root.GetProperty("occurredAt").GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal),
                TryGuid(root, "recordingSetVersionId") ?? Guid.Empty,
                TryGuid(root, "matchId") ?? Guid.Empty,
                TryGuid(root, "teamId") ?? Guid.Empty,
                ReadMembers(root));

            NotificationOutcome outcome;
            try
            {
                outcome = await scope.ServiceProvider.GetRequiredService<HandleRecordingsFinalizedNotificationHandler>()
                    .HandleAsync(notification, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning("Recordings-finalized handling failed: {Category}", WorkerLoop.Categorize(ex));
                await msg.NakAsync(nak, cancellationToken: ct).ConfigureAwait(false);
                return;
            }

            if (outcome is NotificationOutcome.DependencyUnavailable or NotificationOutcome.WorkflowUnresolvable)
            {
                await msg.NakAsync(nak, cancellationToken: ct).ConfigureAwait(false);
            }
            else
            {
                await msg.AckAsync(cancellationToken: ct).ConfigureAwait(false);
            }
        }
    }

    private async Task RecordSchemaInvalidAsync(IServiceProvider sp, Guid eventId, Guid? setId, CancellationToken ct)
    {
        var unitOfWork = sp.GetRequiredService<IUnitOfWork>();
        var receipts = sp.GetRequiredService<INotificationReceiptStore>();
        await using var scope = await unitOfWork.BeginAsync(ct).ConfigureAwait(false);
        var result = await receipts.TryInsertAsync(
            new NotificationReceiptRecord(eventId, MessageType, setId, "rejected", "schema-invalid", null, timeProvider.GetUtcNow()), ct).ConfigureAwait(false);
        if (result.Inserted)
        {
            await scope.CommitAsync(ct).ConfigureAwait(false);
        }
        else
        {
            await scope.RollbackAsync(ct).ConfigureAwait(false);
        }
    }

    private static Guid? TryGuid(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object
        && root.TryGetProperty(name, out var p)
        && p.ValueKind == JsonValueKind.String
        && Guid.TryParse(p.GetString(), out var g)
            ? g
            : null;

    private static List<FinalizedNotificationMember> ReadMembers(JsonElement root)
    {
        var members = new List<FinalizedNotificationMember>();
        foreach (var m in root.GetProperty("members").EnumerateArray())
        {
            members.Add(new FinalizedNotificationMember(
                m.GetProperty("position").GetInt32(),
                TryGuid(m, "recordingVersionId") ?? Guid.Empty,
                m.GetProperty("recordingContentDigest").GetString()!,
                TryGuid(m, "timelineMappingId") ?? Guid.Empty,
                m.GetProperty("timelineMappingDigest").GetString()!));
        }

        return members;
    }
}
