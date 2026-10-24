using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using SocAlytics.Analysts.Manager.Hosting;
using SocAlytics.Analysts.Manager.Runtime;
using SocAlytics.Analysts.Manager.Runtime.Simulated;
using SocAlytics.Analysts.Manager.Work;
using SocAlytics.Platform.Application.Analysis.Commands;
using SocAlytics.Platform.Application.Recordings;
using SocAlytics.Platform.EndToEnd.Tests.Support;
using SocAlytics.Platform.Integration.Tests.IdentityAccess.Support;
using SocAlytics.Platform.Integration.Tests.Recordings.Support;
using Xunit;

namespace SocAlytics.Platform.EndToEnd.Tests;

[Collection("EndToEnd")]
public sealed class RunSupersessionEndToEndTests
{
    private const int LeaseSeconds = 6;
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(LeaseSeconds / 3.0);

    [Fact]
    public async Task Finalizing_a_newer_recording_set_supersedes_the_running_run_and_the_Manager_stops_and_cleans_up_without_reporting()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        var ct = cts.Token;
        var started = await EndToEndHarness.StartAsync(ct);
        if (started is null)
        {
            Assert.Skip("SoftHSM2 (libsofthsm2 and softhsm2-util) is required on Linux for the Manager's device key.");
            return;
        }

        await using var harness = started;
        var manager = await harness.StartManagerAsync(ct, TimeSpan.FromMinutes(2));
        var control = manager.Services.GetRequiredService<IManagerControl>();

        var hierarchy = await new ClubHierarchyBuilder(harness.Platform, harness.Admin).CreateAsync(ct);
        using var coach = await SignInCoachAsync(harness, hierarchy.TeamId, ct);
        var runId = await WorkExecutionEndToEndTests.SeedReadyJobAsync(
            harness, ct, LeaseSeconds, LeaseSeconds * 4, hierarchy.TeamId, hierarchy.MatchId);

        await EndToEndHarness.WaitAsync(
            async () => await ScalarAsync(harness, "SELECT count(*) FROM socalytics.analysis_execution_attempts a JOIN socalytics.analysis_logical_jobs j ON j.id = a.logical_job_id WHERE j.run_id = @runId AND a.state = 'Active'", runId, ct) == 1,
            TimeSpan.FromSeconds(60), "The attempt was never claimed.", ct);
        await EndToEndHarness.WaitAsync(
            () => control.GetStatus().ActiveAttempts.Count == 1,
            TimeSpan.FromSeconds(30), "The Manager does not show its active attempt.", ct);

        var adapter = (SimulatedRuntimeAdapter)manager.Services.GetRequiredService<RuntimeAdapterCatalog>().Find(SimulatedRuntimeAdapter.TestProfile)!;
        await EndToEndHarness.WaitAsync(
            () => adapter.Attempts.Count == 1, TimeSpan.FromSeconds(30), "The simulated container was never started.", ct);
        var execution = adapter.Attempts.Single();
        var olderJobId = await GuidScalarAsync(harness, "SELECT id FROM socalytics.analysis_logical_jobs WHERE run_id = @runId", runId, ct);
        var attemptId = Guid.Parse(execution.Launch.AttemptId);
        var attemptDirectory = UpstreamStager.DirectoryFor(attemptId);
        Directory.Exists(attemptDirectory).ShouldBeTrue();
        execution.Stopped.ShouldBeFalse();

        using (var current = await coach.SendAsync(HttpMethod.Get, $"/api/v1/analysis/runs/{runId}", null, ct))
        {
            var run = await current.Content.ReadFromJsonAsync<JsonElement>(ct);
            run.GetProperty("state").GetString().ShouldBe("Running");
        }

        // A Coach finalizes a newer recording set version through the member API.
        var set = await RecordingFinalizationHelper.FinalizeAsync(coach, hierarchy.MatchId, 1, ct);
        await using (var scope = harness.Platform.Services.CreateAsyncScope())
        {
            var lineage = (await scope.ServiceProvider.GetRequiredService<IRecordingSetLookup>().GetAsync(set.RecordingSetVersionId, ct))!;
            var notification = new RecordingsFinalizedNotification(
                Guid.NewGuid(), "1.0.0", DateTimeOffset.UtcNow, lineage.RecordingSetVersionId, lineage.MatchId, lineage.TeamId,
                [.. lineage.Members.Select(m => new FinalizedNotificationMember(
                    m.Position, m.RecordingVersionId, m.RecordingContentDigest, m.TimelineMappingId, m.TimelineMappingDigest))]);
            var outcome = await scope.ServiceProvider.GetRequiredService<HandleRecordingsFinalizedNotificationHandler>().HandleAsync(notification, ct);
            outcome.ShouldBeOfType<NotificationOutcome.RunCreated>();
        }

        var finalizedAt = DateTime.UtcNow;

        await EndToEndHarness.WaitAsync(
            async () => await ScalarAsync(harness, "SELECT count(*) FROM socalytics.analysis_runs WHERE id = @runId AND state = 'Cancelled' AND cancellation_cause = 'superseded'", runId, ct) == 1,
            TimeSpan.FromSeconds(60), "The older run was not superseded.\n"
                + await WorkExecutionEndToEndTests.DiagnosticsAsync(harness, runId, ct), ct);
        (await ScalarAsync(harness, "SELECT count(*) FROM socalytics.analysis_run_supersessions WHERE run_id = @runId", runId, ct)).ShouldBe(1);

        using (var read = await coach.SendAsync(HttpMethod.Get, $"/api/v1/analysis/runs/{runId}", null, ct))
        {
            var body = await read.Content.ReadFromJsonAsync<JsonElement>(ct);
            body.GetProperty("state").GetString().ShouldBe("Cancelled");
        }

        // The attempt is closed in the superseding transaction, so the next heartbeat is refused as obsolete.
        await EndToEndHarness.WaitAsync(
            () => execution.Stopped && !Directory.Exists(attemptDirectory) && !HoldsJob(control, olderJobId),
            TimeSpan.FromSeconds(30), "The Manager did not stop the container and delete the attempt directory.\n"
                + await WorkExecutionEndToEndTests.DiagnosticsAsync(harness, runId, ct), ct);
        (DateTime.UtcNow - finalizedAt).ShouldBeLessThanOrEqualTo(TimeSpan.FromSeconds(30) + HeartbeatInterval * 2);
        execution.CleanedUp.ShouldBeTrue();

        // Nothing of the superseded run is accepted afterwards, and the closed attempt keeps its cancelled state.
        await Task.Delay(HeartbeatInterval * 2, ct);
        (await WorkExecutionEndToEndTests.CountAcceptedAsync(harness, runId, ct)).ShouldBe(0);
        (await ScalarAsync(harness, "SELECT count(*) FROM socalytics.analysis_execution_attempts a JOIN socalytics.analysis_logical_jobs j ON j.id = a.logical_job_id WHERE j.run_id = @runId AND a.state = 'Cancelled' AND a.close_reason = 'run-cancelled'", runId, ct)).ShouldBe(1);
        (await ScalarAsync(harness, "SELECT count(*) FROM socalytics.analysis_execution_attempts a JOIN socalytics.analysis_logical_jobs j ON j.id = a.logical_job_id WHERE j.run_id = @runId", runId, ct)).ShouldBe(1);
        HoldsJob(control, olderJobId).ShouldBeFalse();
    }

    private static async Task<ApiSession> SignInCoachAsync(EndToEndHarness harness, Guid teamId, CancellationToken ct)
    {
        var coachId = await TestMembers.SeedAsync(harness.Database, "e2e-coach", cancellationToken: ct);
        await using (var connection = new NpgsqlConnection(harness.Database.AppConnectionString))
        {
            await connection.OpenAsync(ct);
            await using var command = new NpgsqlCommand(
                "INSERT INTO socalytics.team_role_assignment (member_account_id, team_id, role, assigned_at, assigned_by_account_id) VALUES (@m, @t, 'coach', now(), @m)", connection);
            command.Parameters.AddWithValue("m", coachId);
            command.Parameters.AddWithValue("t", teamId);
            await command.ExecuteNonQueryAsync(ct);
        }

        return await ApiSession.SignInAsync(harness.Platform, "e2e-coach", TestMembers.DefaultPassword, ct);
    }

    private static bool HoldsJob(IManagerControl control, Guid jobId) =>
        control.GetStatus().ActiveAttempts.Any(a => a.LogicalJobId == jobId);

    private static async Task<Guid> GuidScalarAsync(EndToEndHarness harness, string query, Guid runId, CancellationToken ct)
    {
        await using var sql = new NpgsqlConnection(harness.Database.MigratorConnectionString);
        await sql.OpenAsync(ct);
        await using var command = new NpgsqlCommand(query, sql);
        command.Parameters.AddWithValue("runId", runId);
        return (Guid)(await command.ExecuteScalarAsync(ct))!;
    }

    private static async Task<long> ScalarAsync(EndToEndHarness harness, string query, Guid runId, CancellationToken ct)
    {
        await using var sql = new NpgsqlConnection(harness.Database.MigratorConnectionString);
        await sql.OpenAsync(ct);
        await using var command = new NpgsqlCommand(query, sql);
        command.Parameters.AddWithValue("runId", runId);
        return (long)(await command.ExecuteScalarAsync(ct))!;
    }
}
