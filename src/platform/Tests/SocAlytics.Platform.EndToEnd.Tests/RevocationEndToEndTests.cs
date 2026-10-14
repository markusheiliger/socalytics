using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using SocAlytics.Analysts.Manager.Hosting;
using SocAlytics.Platform.EndToEnd.Tests.Support;
using Xunit;

namespace SocAlytics.Platform.EndToEnd.Tests;

[Collection("EndToEnd")]
public sealed class RevocationEndToEndTests
{
    private const int LeaseSeconds = 3;
    private const int StaleTimeoutSeconds = 6;

    [Fact]
    public async Task Revoking_a_Manager_with_an_active_attempt_fences_it_stops_its_work_and_lets_a_second_Manager_complete_the_job()
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
        var first = await harness.StartManagerAsync(ct, TimeSpan.FromMinutes(2));
        var firstControl = first.Services.GetRequiredService<IManagerControl>();
        var firstManagerId = firstControl.GetStatus().ManagerId;
        var runId = await WorkExecutionEndToEndTests.SeedReadyJobAsync(harness, ct, LeaseSeconds, StaleTimeoutSeconds);

        await EndToEndHarness.WaitAsync(
            async () => (await AttemptsAsync(harness, runId, ct)).Any(a => a.State == "Active"),
            TimeSpan.FromSeconds(60), "The first attempt was never claimed.", ct);
        await EndToEndHarness.WaitAsync(
            () => firstControl.GetStatus().ActiveAttempts.Count == 1,
            TimeSpan.FromSeconds(30), "The Manager does not show its active attempt.", ct);
        (await ScalarAsync(harness, "SELECT count(*) FROM socalytics.analyst_manager_queue_credential WHERE revoked_at IS NULL", ct)).ShouldBeGreaterThan(0);

        await harness.RevokeManagerAsync(first, ct);

        // Fenced in the same step as the revocation (SC-005).
        var afterRevocation = await AttemptsAsync(harness, runId, ct);
        afterRevocation.Single().State.ShouldBe("Stale");
        afterRevocation.Single().ManagerId.ShouldBe(firstManagerId);
        (await ScalarAsync(harness, "SELECT count(*) FROM socalytics.analysis_accepted_results", ct)).ShouldBe(0);

        // The queue access is revoked and pushed to the queue server within 5 seconds (SC-009).
        await EndToEndHarness.WaitAsync(
            async () => await ScalarAsync(
                harness,
                "SELECT count(*) FROM socalytics.analyst_manager_queue_credential WHERE revoked_at IS NOT NULL AND revocation_pushed_at IS NULL", ct) == 0,
            TimeSpan.FromSeconds(5), "The queue revocation was not pushed in time.", ct);
        (await ScalarAsync(
            harness, "SELECT count(*) FROM socalytics.analyst_manager_queue_credential WHERE revoked_at IS NOT NULL AND revocation_pushed_at IS NOT NULL", ct))
            .ShouldBeGreaterThan(0);

        // The Manager stops its simulated work and returns to Unregistered within 10 seconds of observing it (SC-005).
        await EndToEndHarness.WaitAsync(
            () => firstControl.GetStatus() is { RegistrationState: "unregistered", ActiveAttempts.Count: 0 },
            TimeSpan.FromSeconds(30), "The revoked Manager did not return to Unregistered.", ct);

        await harness.StartManagerAsync(ct, TimeSpan.FromSeconds(1));
        var secondManagerId = harness.Control.GetStatus().ManagerId;
        secondManagerId.ShouldNotBe(firstManagerId);

        try
        {
            await EndToEndHarness.WaitAsync(
                async () => await WorkExecutionEndToEndTests.CountAcceptedAsync(harness, runId, ct) > 0,
                TimeSpan.FromSeconds(90), "No accepted result arrived in time.", ct);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException("No accepted result arrived in time.\n" + await WorkExecutionEndToEndTests.DiagnosticsAsync(harness, runId, ct));
        }

        var attempts = await AttemptsAsync(harness, runId, ct);
        attempts.Select(a => a.Number).ShouldBe([1, 2]);
        attempts[0].State.ShouldBe("Stale");
        attempts[0].ManagerId.ShouldBe(firstManagerId);
        attempts[1].State.ShouldBe("Accepted");
        attempts[1].ManagerId.ShouldBe(secondManagerId);
        (await WorkExecutionEndToEndTests.CountAcceptedAsync(harness, runId, ct)).ShouldBe(1);
    }

    private sealed record AttemptRow(int Number, string State, string ManagerId);

    private static async Task<List<AttemptRow>> AttemptsAsync(EndToEndHarness harness, Guid runId, CancellationToken ct)
    {
        await using var sql = new NpgsqlConnection(harness.Database.MigratorConnectionString);
        await sql.OpenAsync(ct);
        await using var command = new NpgsqlCommand(
            """
            SELECT a.attempt_number, a.state, a.manager_id::text
            FROM socalytics.analysis_execution_attempts a
            JOIN socalytics.analysis_logical_jobs j ON j.id = a.logical_job_id
            WHERE j.run_id = @runId
            ORDER BY a.attempt_number
            """, sql);
        command.Parameters.AddWithValue("runId", runId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var rows = new List<AttemptRow>();
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new AttemptRow(reader.GetInt32(0), reader.GetString(1), reader.GetString(2)));
        }

        return rows;
    }

    private static async Task<long> ScalarAsync(EndToEndHarness harness, string query, CancellationToken ct)
    {
        await using var sql = new NpgsqlConnection(harness.Database.MigratorConnectionString);
        await sql.OpenAsync(ct);
        await using var command = new NpgsqlCommand(query, sql);
        return (long)(await command.ExecuteScalarAsync(ct))!;
    }
}
