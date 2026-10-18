using Npgsql;
using Shouldly;
using SocAlytics.Analysts.Manager.Runtime.Simulated;
using SocAlytics.Platform.EndToEnd.Tests.Support;
using Xunit;

namespace SocAlytics.Platform.EndToEnd.Tests;

[Collection("EndToEnd")]
public sealed class InvalidResultEndToEndTests
{
    [Fact]
    public async Task A_lineage_mismatch_fails_each_attempt_with_its_reason_until_the_budget_is_spent()
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
        await harness.StartManagerAsync(ct, TimeSpan.FromSeconds(1), resultMode: SimulatedResultModes.LineageMismatch);
        var runId = await WorkExecutionEndToEndTests.SeedReadyJobAsync(harness, ct);

        await Wait(harness, runId, async () => await ScalarAsync(harness, "SELECT count(*) FROM socalytics.analysis_logical_jobs WHERE run_id = @runId AND state = 'Failed'", runId, ct) > 0,
            TimeSpan.FromSeconds(150), "The Logical Job did not fail after its attempts.", ct);

        (await WorkExecutionEndToEndTests.CountAcceptedAsync(harness, runId, ct)).ShouldBe(0);
        var attempts = await ScalarAsync(harness, "SELECT count(*) FROM socalytics.analysis_execution_attempts a JOIN socalytics.analysis_logical_jobs j ON j.id = a.logical_job_id WHERE j.run_id = @runId AND a.state = 'Failed'", runId, ct);
        attempts.ShouldBe(3, "The first attempt fails and new attempts follow until the budget of three is spent.");
        var reasons = await ScalarAsync(harness, "SELECT count(*) FROM socalytics.analysis_completion_outcomes o JOIN socalytics.analysis_logical_jobs j ON j.id = o.logical_job_id WHERE j.run_id = @runId AND o.outcome = 'failed' AND o.reason = 'result-lineage-mismatch'", runId, ct);
        reasons.ShouldBe(3);
    }

    [Fact]
    public async Task A_contract_invalid_manifest_is_never_uploaded_and_the_attempt_is_completed_as_failed()
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
        await harness.StartManagerAsync(ct, TimeSpan.FromSeconds(1), resultMode: SimulatedResultModes.ContractInvalid);
        var runId = await WorkExecutionEndToEndTests.SeedReadyJobAsync(harness, ct);

        await Wait(harness, runId, async () => await ScalarAsync(harness, "SELECT count(*) FROM socalytics.analysis_execution_attempts a JOIN socalytics.analysis_logical_jobs j ON j.id = a.logical_job_id WHERE j.run_id = @runId AND a.state = 'Failed'", runId, ct) > 0,
            TimeSpan.FromSeconds(60), "No attempt was completed as failed.", ct);

        (await WorkExecutionEndToEndTests.CountAcceptedAsync(harness, runId, ct)).ShouldBe(0);
        (await ScalarAsync(harness, "SELECT count(*) FROM socalytics.analysis_completion_outcomes o JOIN socalytics.analysis_logical_jobs j ON j.id = o.logical_job_id WHERE j.run_id = @runId AND o.outcome = 'failure-recorded'", runId, ct))
            .ShouldBeGreaterThan(0);

        using var s3 = harness.Store.CreateS3Client();
        var listed = await s3.ListObjectsV2Async(
            new Amazon.S3.Model.ListObjectsV2Request { BucketName = harness.Store.Bucket, Prefix = $"results/{runId}/" }, ct);
        (listed.S3Objects ?? []).ShouldBeEmpty("Nothing is stored under the result location of a contract-invalid attempt.");
    }

    [Fact]
    public async Task Facts_outside_the_validity_window_fail_the_attempt_and_no_fact_is_readable()
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
        await harness.StartManagerAsync(ct, TimeSpan.FromSeconds(1), resultMode: SimulatedResultModes.InvalidFacts);
        var runId = await WorkExecutionEndToEndTests.SeedReadyJobAsync(harness, ct);

        await Wait(harness, runId,
            async () => await ScalarAsync(harness, "SELECT count(*) FROM socalytics.analysis_completion_outcomes o JOIN socalytics.analysis_logical_jobs j ON j.id = o.logical_job_id WHERE j.run_id = @runId AND o.outcome = 'failed' AND o.reason = 'result-facts-out-of-window'", runId, ct) > 0,
            TimeSpan.FromSeconds(60), "The attempt was not failed with result-facts-out-of-window.", ct);

        (await WorkExecutionEndToEndTests.CountAcceptedAsync(harness, runId, ct)).ShouldBe(0);
        (await ScalarAsync(harness, "SELECT count(*) FROM socalytics.analysis_indexed_facts WHERE run_id = @runId", runId, ct))
            .ShouldBe(0, "No fact of a rejected result is readable.");
    }

    private static async Task Wait(EndToEndHarness harness, Guid runId, Func<Task<bool>> condition, TimeSpan timeout, string failure, CancellationToken ct)
    {
        try
        {
            await EndToEndHarness.WaitAsync(condition, timeout, failure, ct);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException(failure + "\n" + await WorkExecutionEndToEndTests.DiagnosticsAsync(harness, runId, ct));
        }
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
