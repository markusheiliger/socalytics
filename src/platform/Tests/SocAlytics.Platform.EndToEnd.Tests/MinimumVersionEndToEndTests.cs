using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;
using Shouldly;
using SocAlytics.Analysts.Manager.Hosting;
using SocAlytics.Platform.Application.Registry.StatusReporting;
using SocAlytics.Platform.EndToEnd.Tests.Support;
using Xunit;

namespace SocAlytics.Platform.EndToEnd.Tests;

[Collection("EndToEnd")]
public sealed class MinimumVersionEndToEndTests
{
    private const string AboveManager = "99.0.0";

    [Fact]
    public async Task A_Manager_below_the_minimum_finishes_its_attempt_claims_nothing_new_and_resumes_when_the_minimum_is_lowered()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(8));
        var ct = cts.Token;
        var started = await EndToEndHarness.StartAsync(ct);
        if (started is null)
        {
            Assert.Skip("SoftHSM2 (libsofthsm2 and softhsm2-util) is required on Linux for the Manager's device key.");
            return;
        }

        await using var harness = started;
        var checkIntervalSeconds = harness.Platform.Services.GetRequiredService<IOptions<StatusReportingOptions>>().Value.IntervalSeconds;
        await harness.StartManagerAsync(ct, TimeSpan.FromSeconds(6));
        var control = harness.Manager.Services.GetRequiredService<IManagerControl>();
        var managerId = control.GetStatus().ManagerId;
        control.GetStatus().UpdateRequired.ShouldBeNull();

        var firstRun = await WorkExecutionEndToEndTests.SeedReadyJobAsync(harness, ct);
        await EndToEndHarness.WaitAsync(
            () => control.GetStatus().ActiveAttempts.Count == 1,
            TimeSpan.FromSeconds(60), "The first attempt was never claimed.", ct);

        harness.SetMinimumManagerVersion(AboveManager);

        // The Manager learns the minimum with its next status check and shows the notice while it keeps running its attempt.
        await EndToEndHarness.WaitAsync(
            () => control.GetStatus().UpdateRequired is { Required: true },
            TimeSpan.FromSeconds(checkIntervalSeconds + 10), "The Manager did not show the update notice.", ct);
        control.GetStatus().UpdateRequired!.MinimumVersion.ShouldBe(AboveManager);

        try
        {
            await EndToEndHarness.WaitAsync(
                async () => await WorkExecutionEndToEndTests.CountAcceptedAsync(harness, firstRun, ct) > 0,
                TimeSpan.FromSeconds(60), "The running attempt did not finish.", ct);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException("The running attempt did not finish.\n" + await WorkExecutionEndToEndTests.DiagnosticsAsync(harness, firstRun, ct));
        }

        var secondRun = await WorkExecutionEndToEndTests.SeedReadyJobAsync(harness, ct);
        await Task.Delay(TimeSpan.FromSeconds(checkIntervalSeconds + 15), ct);
        (await AttemptCountAsync(harness, secondRun, ct)).ShouldBe(0);
        control.GetStatus().UpdateRequired.ShouldNotBeNull();
        control.GetStatus().ActiveAttempts.Count.ShouldBe(0);

        harness.SetMinimumManagerVersion(null);

        await EndToEndHarness.WaitAsync(
            async () => await AttemptCountAsync(harness, secondRun, ct) > 0,
            TimeSpan.FromSeconds(checkIntervalSeconds + 10), "The Manager did not claim work after the minimum was lowered.", ct);
        control.GetStatus().UpdateRequired.ShouldBeNull();

        try
        {
            await EndToEndHarness.WaitAsync(
                async () => await WorkExecutionEndToEndTests.CountAcceptedAsync(harness, secondRun, ct) > 0,
                TimeSpan.FromSeconds(60), "The second job was not completed.", ct);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException("The second job was not completed.\n" + await WorkExecutionEndToEndTests.DiagnosticsAsync(harness, secondRun, ct));
        }

        // The same registration served both jobs: no re-registration.
        control.GetStatus().ManagerId.ShouldBe(managerId);
        control.GetStatus().RegistrationState.ShouldBe("active");
        (await ScalarAsync(harness, "SELECT count(*) FROM socalytics.analyst_manager_registration", ct)).ShouldBe(1);
    }

    private static Task<long> AttemptCountAsync(EndToEndHarness harness, Guid runId, CancellationToken ct) =>
        ScalarAsync(
            harness,
            $"SELECT count(*) FROM socalytics.analysis_execution_attempts a JOIN socalytics.analysis_logical_jobs j ON j.id = a.logical_job_id WHERE j.run_id = '{runId}'",
            ct);

    private static async Task<long> ScalarAsync(EndToEndHarness harness, string query, CancellationToken ct)
    {
        await using var sql = new NpgsqlConnection(harness.Database.MigratorConnectionString);
        await sql.OpenAsync(ct);
        await using var command = new NpgsqlCommand(query, sql);
        return (long)(await command.ExecuteScalarAsync(ct))!;
    }
}
