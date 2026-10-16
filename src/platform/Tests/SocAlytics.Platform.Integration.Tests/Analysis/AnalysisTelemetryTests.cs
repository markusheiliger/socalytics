using System.Diagnostics.Metrics;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;
using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Application.Analysis.Commands;
using SocAlytics.Platform.Application.Messaging;
using SocAlytics.Platform.Application.Telemetry;
using SocAlytics.Platform.Integration.Tests.Analysis.Support;
using SocAlytics.Platform.Integration.Tests.Infrastructure;
using SocAlytics.Platform.Integration.Tests.Recordings;
using SocAlytics.Platform.Migrator;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.Analysis;

public sealed class AnalysisTelemetryTests(PostgresContainerFixture postgres, NatsContainerFixture nats, RustFsContainerFixture rustFs)
{
    private sealed class ThrowingTransport : IMessageTransport
    {
        public Task<TransportPublishResult> PublishAsync(TransportMessage message, CancellationToken cancellationToken) =>
            throw new MessageTransportException(TransportFailureCategories.PublishRejected);
    }

    private sealed class OkTransport : IMessageTransport
    {
        public Task<TransportPublishResult> PublishAsync(TransportMessage message, CancellationToken cancellationToken) =>
            Task.FromResult(TransportPublishResult.Published);
    }

    private static MeterListener Listen(List<(string Name, long Value, Dictionary<string, object?> Tags)> sink)
    {
        var listener = new MeterListener
        {
            InstrumentPublished = (i, l) =>
            {
                if (i.Meter.Name is AnalysisMetrics.MeterName or MessagingMetrics.MeterName)
                {
                    l.EnableMeasurementEvents(i);
                }
            },
        };
        listener.SetMeasurementEventCallback<long>((i, v, t, _) => { lock (sink) { sink.Add((i.Name, v, t.ToArray().ToDictionary(x => x.Key, x => x.Value))); } });
        listener.SetMeasurementEventCallback<double>((i, v, t, _) => { lock (sink) { sink.Add((i.Name, (long)v, t.ToArray().ToDictionary(x => x.Key, x => x.Value))); } });
        listener.Start();
        return listener;
    }

    private async Task<(IsolatedDatabase Db, AnalysisTestHost Host)> StartAsync(CancellationToken ct)
    {
        var db = await postgres.CreateDatabaseAsync(ct);
        (await MigratorHarness.RunAsync(db, TestMigrationCatalogs.Platform(), ct)).ExitCode.ShouldBe(MigratorExitCode.Success);
        var host = new AnalysisTestHost(db, nats, rustFs, managerAuthentication: ManagerAuthentication.Test);
        await host.StartAsync(ct);
        return (db, host);
    }

    [Fact]
    public async Task Instruments_are_emitted_without_ids_or_secrets()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, host) = await StartAsync(ct);
        await using var hostScope = host;
        var sink = new List<(string Name, long Value, Dictionary<string, object?> Tags)>();
        using var listener = Listen(sink);

        var (job, occ) = await ExecutionAttemptTests.SeedJobAsync(db, "Ready", ct);
        using var m = new TestManagerClient(host.Factory);
        using var claim = await m.ClaimAsync(job, occ, "k1", ct: ct);
        claim.StatusCode.ShouldBe(HttpStatusCode.Created);
        var body = JsonDocument.Parse(await claim.Content.ReadAsStringAsync(ct)).RootElement;
        var attemptId = body.GetProperty("attemptId").GetGuid();

        using var bad = await m.HeartbeatAsync(attemptId, job, 987654321, ct);
        bad.StatusCode.ShouldBe(HttpStatusCode.Conflict);

        host.Time.Advance(TimeSpan.FromMinutes(5));
        await using (var scope = host.Services.CreateAsyncScope())
        {
            (await scope.ServiceProvider.GetRequiredService<ExpireStaleAttemptsHandler>().HandleAsync(ct)).ShouldBe(1);
        }

        await using (var scope = host.Services.CreateAsyncScope())
        {
            var sp = scope.ServiceProvider;
            await using (var uow = await sp.GetRequiredService<IUnitOfWork>().BeginAsync(ct))
            {
                var payload = JsonDocument.Parse("{\"a\":1}").RootElement.Clone();
                (await sp.GetRequiredService<IOutbox>().EnqueueAsync(
                    new OutboxMessageDraft(Guid.NewGuid(), OutboxMessageTypes.AnalysisReadyWork, "1.0.0", "analysis.ready-work", payload, Guid.NewGuid()), ct)).ShouldBeTrue();
                await uow.CommitAsync(ct);
            }

            PublishOutboxBatch Make(IMessageTransport t) => new(
                sp.GetRequiredService<IUnitOfWork>(), sp.GetRequiredService<IOutboxPublicationStore>(), t,
                sp.GetRequiredService<IOptions<OutboxPublicationOptions>>(), host.Time);
            await Make(new ThrowingTransport()).HandleAsync(ct);
            host.Time.Advance(TimeSpan.FromHours(1));
            await Make(new OkTransport()).HandleAsync(ct);
        }

        listener.RecordObservableInstruments();
        var seen = sink.Select(s => s.Name).ToHashSet();
        foreach (var name in new[]
        {
            "socalytics.analysis.lease_expiries", "socalytics.analysis.heartbeats_rejected", "socalytics.messaging.published",
            "socalytics.messaging.publish_failed", "socalytics.messaging.publication_lag", "socalytics.messaging.outbox_pending",
            "socalytics.messaging.outbox_failed", "socalytics.messaging.outbox_oldest_pending_age",
        })
        {
            seen.ShouldContain(name);
        }

        var allowed = new HashSet<string> { "capability", "message_type", "outcome", "failure_category" };
        foreach (var tags in sink.Select(s => s.Tags))
        {
            tags.Keys.ShouldAllBe(k => allowed.Contains(k));
            foreach (var v in tags.Values)
            {
                Guid.TryParse(v as string, out var parsed).ShouldBeFalse();
                parsed.ShouldBe(Guid.Empty);
                (v as string ?? "").ShouldNotContain("https://");
            }
        }
    }
}
