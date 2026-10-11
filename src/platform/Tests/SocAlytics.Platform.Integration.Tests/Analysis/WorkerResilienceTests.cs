using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Shouldly;
using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Infrastructure.Workers;
using SocAlytics.Platform.Integration.Tests.IdentityAccess.Support;
using SocAlytics.Platform.Integration.Tests.Infrastructure;
using SocAlytics.Platform.Integration.Tests.Recordings;
using SocAlytics.Platform.Migrator;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.Analysis;

[Collection("NatsFaultInjection")]
public sealed class WorkerResilienceTests(NatsContainerFixture nats, PostgresContainerFixture postgres, RustFsContainerFixture rustFs)
{
    private PlatformApiFactory Create(IsolatedDatabase db, string? natsConnection) => new(
        db,
        clubBootstrap: new Dictionary<string, string?>
        {
            ["ClubDisplayName"] = "Worker Club",
            ["FirstClubAdmin:AccountName"] = "worker-admin",
            ["FirstClubAdmin:InitialPassword"] = "Worker-Initial-Pass-1234",
        },
        recordingUpload: RecordingTestData.WithKeyPrefix(RecordingTestData.NewKeyPrefix()),
        objectStorage: new Dictionary<string, string?>
        {
            ["ServiceUrl"] = rustFs.ServiceUrl,
            ["Region"] = rustFs.Region,
            ["AccessKey"] = rustFs.AccessKey,
            ["SecretKey"] = rustFs.SecretKey,
            ["Bucket"] = rustFs.Bucket,
        },
        extraSettings: new Dictionary<string, string?>
        {
            ["Analysis:Workers:Enabled"] = "true",
            ["ConnectionStrings:nats"] = natsConnection,
        });

    private async Task<IsolatedDatabase> MigratedAsync(CancellationToken ct)
    {
        var db = await postgres.CreateDatabaseAsync(ct);
        (await MigratorHarness.RunAsync(db, TestMigrationCatalogs.Platform(), ct)).ExitCode.ShouldBe(MigratorExitCode.Success);
        return db;
    }

    private static async Task<int> AttemptsAsync(IsolatedDatabase db, Guid id, CancellationToken ct)
    {
        await using var c = new NpgsqlConnection(db.MigratorConnectionString);
        await c.OpenAsync(ct);
        return await c.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT attempt_count FROM socalytics.outbox_messages WHERE id = @id AND state = 'pending'", new { id }, cancellationToken: ct));
    }

    [Fact]
    public async Task Host_without_nats_keeps_running_and_publishes_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await MigratedAsync(ct);
        var id = Guid.NewGuid();
        await using (var c = new NpgsqlConnection(db.MigratorConnectionString))
        {
            await c.OpenAsync(ct);
            await c.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO socalytics.outbox_messages (id, message_type, contract_version, subject, payload, causation_id, state, created_at)
                VALUES (@id, 'analysis.ready-work', '1.0.0', 'analysis.jobs.ready.test', '{}'::jsonb, @id, 'pending', now())
                """,
                new { id }, cancellationToken: ct));
        }

        await using var factory = Create(db, null);
        await factory.WaitUntilHealthyAsync(ct);
        await Task.Delay(TimeSpan.FromSeconds(5), ct);
        using var client = factory.CreateApiClient();
        using var health = await client.GetAsync("/health", ct);
        health.StatusCode.ShouldBe(System.Net.HttpStatusCode.OK);
        (await health.Content.ReadAsStringAsync(ct)).ShouldBe("Healthy");
        using var alive = await client.GetAsync("/alive", ct);
        alive.StatusCode.ShouldBe(System.Net.HttpStatusCode.OK);
        (await AttemptsAsync(db, id, ct)).ShouldBe(0);
    }

    [Fact]
    public async Task Host_with_stopped_nats_keeps_running_degraded()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await MigratedAsync(ct);
        await nats.StopAsync();
        try
        {
            await using var factory = Create(db, nats.ConnectionString);
            using var client = factory.CreateApiClient();
            await Task.Delay(TimeSpan.FromSeconds(4), ct);
            using var health = await client.GetAsync("/health", ct);
            health.StatusCode.ShouldBe(System.Net.HttpStatusCode.OK);
            (await health.Content.ReadAsStringAsync(ct)).ShouldBe("Degraded");
        }
        finally
        {
            await nats.StartAsync();
        }
    }

    [Fact]
    public async Task Worker_loop_logs_transport_failure_and_retries()
    {
        var ct = TestContext.Current.CancellationToken;
        var calls = 0;
        var loop = new WorkerLoop("test", TimeProvider.System, NullLogger.Instance,
            TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(50));
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        await loop.RunAsync(_ =>
        {
            if (Interlocked.Increment(ref calls) >= 3)
            {
                cts.Cancel();
            }

            throw new MessageTransportException(TransportFailureCategories.TransportUnavailable);
        }, cts.Token);
        calls.ShouldBeGreaterThanOrEqualTo(3);
    }
}
