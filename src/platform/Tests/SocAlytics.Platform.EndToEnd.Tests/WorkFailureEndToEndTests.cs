using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;
using Shouldly;
using SocAlytics.Analysts.Manager.Runtime.Simulated;
using SocAlytics.Platform.EndToEnd.Tests.Support;
using Xunit;

namespace SocAlytics.Platform.EndToEnd.Tests;

[Collection("EndToEnd")]
public sealed class WorkFailureEndToEndTests
{
    // Heartbeats every lease / 3 = 1 second; the platform expires an attempt after the stale timeout without a renewal.
    private const int LeaseSeconds = 3;
    private const int StaleTimeoutSeconds = 6;

    private sealed record AttemptRow(Guid Id, Guid LogicalJobId, int Number, string State, string ManagerId);

    [Fact]
    public async Task A_simulated_failure_leads_to_a_second_attempt_and_exactly_one_accepted_result()
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
        var manager = await harness.StartManagerAsync(ct, TimeSpan.FromSeconds(3), SimulatedOutcome.Fail);
        var runId = await WorkExecutionEndToEndTests.SeedReadyJobAsync(harness, ct, LeaseSeconds, StaleTimeoutSeconds);

        await WaitForAttemptsAsync(harness, runId, rows => rows.Any(a => a.State == "Failed"), "The first attempt did not fail.", ct);
        var behavior = manager.Services.GetRequiredService<IOptions<SimulatedAnalystOptions>>().Value;
        foreach (var capability in behavior.Capabilities.Values)
        {
            capability.Outcome = SimulatedOutcome.Succeed;
        }

        var attempts = await WaitForAcceptedAsync(harness, runId, TimeSpan.FromSeconds(90), ct);

        attempts.Select(a => a.Number).ShouldBe([1, 2]);
        attempts[0].State.ShouldBe("Failed");
        attempts[1].State.ShouldBe("Accepted");
        attempts.Select(a => a.LogicalJobId).Distinct().Count().ShouldBe(1);
        (await WorkExecutionEndToEndTests.CountAcceptedAsync(harness, runId, ct)).ShouldBe(1);
    }

    [Fact]
    public async Task A_healthy_attempt_running_longer_than_three_lease_periods_is_never_expired()
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
        var runDuration = TimeSpan.FromSeconds(4 * LeaseSeconds);
        await harness.StartManagerAsync(ct, runDuration);
        var runId = await WorkExecutionEndToEndTests.SeedReadyJobAsync(harness, ct, LeaseSeconds, StaleTimeoutSeconds);

        var firstSeen = await WaitForAttemptsAsync(harness, runId, rows => rows.Count > 0, "The attempt was never claimed.", ct);
        var attemptId = firstSeen[0].Id;
        var observed = DateTime.UtcNow;
        while (DateTime.UtcNow - observed < runDuration - TimeSpan.FromSeconds(2))
        {
            var current = await ReadAttemptsAsync(harness, runId, ct);
            current.ShouldAllBe(a => a.Id == attemptId && a.State == "Active", "A healthy attempt must keep its lease.");
            await Task.Delay(500, ct);
        }

        var attempts = await WaitForAcceptedAsync(harness, runId, TimeSpan.FromSeconds(60), ct);

        attempts.Count.ShouldBe(1);
        attempts[0].Id.ShouldBe(attemptId);
        attempts[0].State.ShouldBe("Accepted");
        (await WorkExecutionEndToEndTests.CountAcceptedAsync(harness, runId, ct)).ShouldBe(1);
    }

    [Fact]
    public async Task A_Manager_stopped_mid_attempt_leaves_the_attempt_to_lease_expiry_and_a_second_Manager_completes_the_same_job()
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
        var firstManagerId = harness.Control.GetStatus().ManagerId;
        var runId = await WorkExecutionEndToEndTests.SeedReadyJobAsync(harness, ct, LeaseSeconds, StaleTimeoutSeconds);

        var claimed = await WaitForAttemptsAsync(harness, runId, rows => rows.Count > 0, "The first attempt was never claimed.", ct);
        claimed[0].ManagerId.ShouldBe(firstManagerId);
        await harness.KillManagerAsync(first);

        await WaitForAttemptsAsync(
            harness, runId, rows => rows.Any(a => a.Number == 1 && a.State == "Stale"),
            "The abandoned attempt was not expired by the lease.", ct);
        (await WorkExecutionEndToEndTests.CountAcceptedAsync(harness, runId, ct)).ShouldBe(0);

        await harness.StartManagerAsync(ct, TimeSpan.FromSeconds(1));
        var secondManagerId = harness.Control.GetStatus().ManagerId;
        secondManagerId.ShouldNotBe(firstManagerId);
        var attempts = await WaitForAcceptedAsync(harness, runId, TimeSpan.FromSeconds(90), ct);

        attempts.Select(a => a.Number).ShouldBe([1, 2]);
        attempts[0].State.ShouldBe("Stale");
        attempts[0].ManagerId.ShouldBe(firstManagerId);
        attempts[1].State.ShouldBe("Accepted");
        attempts[1].ManagerId.ShouldBe(secondManagerId);
        attempts.Select(a => a.LogicalJobId).Distinct().Count().ShouldBe(1);
        (await WorkExecutionEndToEndTests.CountAcceptedAsync(harness, runId, ct)).ShouldBe(1);
    }

    private static async Task<List<AttemptRow>> WaitForAcceptedAsync(EndToEndHarness harness, Guid runId, TimeSpan timeout, CancellationToken ct)
    {
        try
        {
            await EndToEndHarness.WaitAsync(
                async () => await WorkExecutionEndToEndTests.CountAcceptedAsync(harness, runId, ct) > 0,
                timeout, "No accepted result arrived in time.", ct);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException("No accepted result arrived in time.\n" + await WorkExecutionEndToEndTests.DiagnosticsAsync(harness, runId, ct));
        }

        return await ReadAttemptsAsync(harness, runId, ct);
    }

    private static async Task<List<AttemptRow>> WaitForAttemptsAsync(
        EndToEndHarness harness, Guid runId, Func<List<AttemptRow>, bool> condition, string failure, CancellationToken ct)
    {
        try
        {
            await EndToEndHarness.WaitAsync(async () => condition(await ReadAttemptsAsync(harness, runId, ct)), TimeSpan.FromSeconds(60), failure, ct);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException(failure + "\n" + await WorkExecutionEndToEndTests.DiagnosticsAsync(harness, runId, ct));
        }

        return await ReadAttemptsAsync(harness, runId, ct);
    }

    private static async Task<List<AttemptRow>> ReadAttemptsAsync(EndToEndHarness harness, Guid runId, CancellationToken ct)
    {
        await using var sql = new NpgsqlConnection(harness.Database.MigratorConnectionString);
        await sql.OpenAsync(ct);
        await using var command = new NpgsqlCommand(
            """
            SELECT a.id, a.logical_job_id, a.attempt_number, a.state, a.manager_id::text
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
            rows.Add(new AttemptRow(reader.GetGuid(0), reader.GetGuid(1), reader.GetInt32(2), reader.GetString(3), reader.GetString(4)));
        }

        return rows;
    }
}
