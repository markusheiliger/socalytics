using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using SocAlytics.Analysts.Manager.Hosting;
using SocAlytics.Analysts.Manager.Runtime;
using SocAlytics.Analysts.Manager.Runtime.Simulated;
using SocAlytics.Analysts.Manager.Work;
using SocAlytics.Platform.EndToEnd.Tests.Support;
using SocAlytics.Platform.Integration.Tests.IdentityAccess.Support;
using SocAlytics.Platform.Integration.Tests.Recordings.Support;
using Xunit;

namespace SocAlytics.Platform.EndToEnd.Tests;

[Collection("EndToEnd")]
public sealed class RunCancellationEndToEndTests
{
    private const int LeaseSeconds = 6;
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(LeaseSeconds / 3.0);

    [Fact]
    public async Task Cancelling_a_running_run_fences_the_active_attempt_and_the_Manager_stops_and_cleans_up_without_reporting()
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
        var attemptId = Guid.Parse(execution.Launch.AttemptId);
        var attemptDirectory = UpstreamStager.DirectoryFor(attemptId);
        Directory.Exists(attemptDirectory).ShouldBeTrue();
        execution.Stopped.ShouldBeFalse();

        using var current = await coach.SendAsync(HttpMethod.Get, $"/api/v1/analysis/runs/{runId}", null, ct);
        current.StatusCode.ShouldBe(HttpStatusCode.OK);
        var run = await current.Content.ReadFromJsonAsync<JsonElement>(ct);
        run.GetProperty("state").GetString().ShouldBe("Running");
        var version = run.GetProperty("version").GetInt64();

        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/analysis/runs/{runId}/cancellation")
        {
            Content = JsonContent.Create(new { reason = "wrong recordings" }),
        };
        request.Headers.Add("X-CSRF-Token", coach.AntiforgeryToken);
        request.Headers.TryAddWithoutValidation("If-Match", $"\"{version}\"");
        using var cancelled = await coach.Client.SendAsync(request, ct);
        cancelled.StatusCode.ShouldBe(HttpStatusCode.OK, await cancelled.Content.ReadAsStringAsync(ct));
        (await cancelled.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("state").GetString().ShouldBe("Cancelled");
        var cancelledAt = DateTime.UtcNow;

        // The attempt is closed in the cancelling transaction, so the next heartbeat is refused as obsolete.
        await EndToEndHarness.WaitAsync(
            () => execution.Stopped && !Directory.Exists(attemptDirectory) && control.GetStatus().ActiveAttempts.Count == 0,
            TimeSpan.FromSeconds(30), "The Manager did not stop the container and delete the attempt directory.\n"
                + await WorkExecutionEndToEndTests.DiagnosticsAsync(harness, runId, ct), ct);
        (DateTime.UtcNow - cancelledAt).ShouldBeLessThanOrEqualTo(HeartbeatInterval * 2 + TimeSpan.FromSeconds(2));
        execution.CleanedUp.ShouldBeTrue();

        // Nothing is accepted after the cancellation, and the closed attempt keeps its cancelled state.
        await Task.Delay(HeartbeatInterval * 2, ct);
        (await WorkExecutionEndToEndTests.CountAcceptedAsync(harness, runId, ct)).ShouldBe(0);
        (await ScalarAsync(harness, "SELECT count(*) FROM socalytics.analysis_execution_attempts a JOIN socalytics.analysis_logical_jobs j ON j.id = a.logical_job_id WHERE j.run_id = @runId AND a.state = 'Cancelled' AND a.close_reason = 'run-cancelled'", runId, ct)).ShouldBe(1);
        (await ScalarAsync(harness, "SELECT count(*) FROM socalytics.analysis_execution_attempts a JOIN socalytics.analysis_logical_jobs j ON j.id = a.logical_job_id WHERE j.run_id = @runId", runId, ct)).ShouldBe(1);
        control.GetStatus().ActiveAttempts.ShouldBeEmpty();
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

    private static async Task<long> ScalarAsync(EndToEndHarness harness, string query, Guid runId, CancellationToken ct)
    {
        await using var sql = new NpgsqlConnection(harness.Database.MigratorConnectionString);
        await sql.OpenAsync(ct);
        await using var command = new NpgsqlCommand(query, sql);
        command.Parameters.AddWithValue("runId", runId);
        return (long)(await command.ExecuteScalarAsync(ct))!;
    }
}
