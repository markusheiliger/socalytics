using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using SocAlytics.Analysts.Manager.Work;
using SocAlytics.Platform.Application.Analysis.Commands;
using SocAlytics.Platform.Application.Recordings;
using SocAlytics.Platform.EndToEnd.Tests.Support;
using SocAlytics.Platform.Integration.Tests;
using SocAlytics.Platform.Integration.Tests.IdentityAccess.Support;
using SocAlytics.Platform.Integration.Tests.Recordings.Support;
using SocAlytics.Platform.Integration.Tests.Registry.Support;
using Xunit;

namespace SocAlytics.Platform.EndToEnd.Tests;

/// <summary>
/// A finalized synthetic recording is materialized into two 5-second segments and an Analyst that requires video
/// receives each segment's media and has its result accepted (FR-029, SC-006).
/// </summary>
[Collection("EndToEnd")]
public sealed class SegmentMediaEndToEndTests
{
    private const string Prefix = "analysts/";
    private const int PolicySeconds = 5;
    private const int Segments = SyntheticMedia.DurationSeconds / PolicySeconds;

    [Fact]
    public async Task The_Docker_runtime_mounts_the_segment_media_and_both_segment_results_are_accepted()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(8));
        var ct = cts.Token;

        var registry = new OciRegistryContainerFixture();
        await registry.InitializeAsync();
        PushedAnalystImage? image = null;
        string? compatibilityPath = null;
        try
        {
            var settings = ToolchainSettings();
            settings["Registry:Images:RegistryHost"] = registry.RegistryHost;
            settings["Registry:Images:RepositoryPrefix"] = Prefix;
            settings["Registry:Images:UseHttp"] = "true";
            var started = await EndToEndHarness.StartAsync(ct, settings);
            if (started is null)
            {
                Assert.Skip("SoftHSM2 (libsofthsm2 and softhsm2-util) is required on Linux for the Manager's device key.");
                return;
            }

            await using var harness = started;
            image = await TestAnalystImage.BuildAndPushAsync(registry.RegistryHost, Prefix, ct, "media");
            await DockerRuntimeEndToEndTests.DefineDefaultWorkflowAsync(
                harness, image.Reference(registry.RegistryHost), ct, "media", videoPolicy: "required", segmentationSeconds: PolicySeconds);

            using var docker = new DockerEngine();
            compatibilityPath = await DockerRuntimeEndToEndTests.WriteCompatibilityListAsync(docker, ct);
            await harness.StartManagerAsync(ct, dockerCompatibilityListPath: compatibilityPath);

            var runId = await FinalizeAndStartRunAsync(harness, ct);
            await WaitForAcceptedAsync(harness, runId, ct);

            var attempts = await AttemptIdsAsync(harness, runId, ct);
            attempts.Count.ShouldBeGreaterThanOrEqualTo(Segments);
            await EndToEndHarness.WaitAsync(
                () => Task.FromResult(attempts.All(a => !Directory.Exists(UpstreamStager.DirectoryFor(a)))),
                TimeSpan.FromSeconds(30), "The media of an attempt remains after the attempt.", ct);
        }
        finally
        {
            if (image is not null)
            {
                await TestAnalystImage.RemoveAsync(image);
            }

            if (compatibilityPath is not null)
            {
                File.Delete(compatibilityPath);
            }

            await registry.DisposeAsync();
        }
    }

    [Fact]
    public async Task The_simulated_runtime_verifies_the_segment_media_and_both_segment_results_are_accepted()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var ct = cts.Token;

        var settings = ToolchainSettings();
        settings["Analysis:DefaultWorkflow:Nodes:0:VideoPolicy"] = "required";
        settings["Analysis:DefaultWorkflow:SegmentationPolicy:DurationSeconds"] = PolicySeconds.ToString();
        var started = await EndToEndHarness.StartAsync(ct, additionalSettings: settings);
        if (started is null)
        {
            Assert.Skip("SoftHSM2 (libsofthsm2 and softhsm2-util) is required on Linux for the Manager's device key.");
            return;
        }

        await using var harness = started;
        await harness.StartManagerAsync(ct, TimeSpan.FromSeconds(1));

        var runId = await FinalizeAndStartRunAsync(harness, ct);
        await WaitForAcceptedAsync(harness, runId, ct);
    }

    private static Dictionary<string, string?> ToolchainSettings() => new()
    {
        ["Segments:Toolchain:FfmpegPath"] = SyntheticMedia.FfmpegPath,
        ["Segments:Toolchain:FfprobePath"] = Path.Combine(Path.GetDirectoryName(SyntheticMedia.FfmpegPath) ?? string.Empty, "ffprobe"),
    };

    private static async Task<Guid> FinalizeAndStartRunAsync(EndToEndHarness harness, CancellationToken ct)
    {
        var hierarchy = await new ClubHierarchyBuilder(harness.Platform, harness.Admin).CreateAsync(ct);
        var coachId = await TestMembers.SeedAsync(harness.Database, "coach-e2e", cancellationToken: ct);
        await FactReadEndToEndTests.ExecuteAsync(
            harness, "INSERT INTO socalytics.team_role_assignment (member_account_id, team_id, role, assigned_at, assigned_by_account_id) VALUES (@m, @t, 'coach', now(), @m)",
            [("m", coachId), ("t", hierarchy.TeamId)], ct);
        using var coach = await ApiSession.SignInAsync(harness.Platform, "coach-e2e", TestMembers.DefaultPassword, ct);

        using var media = SyntheticMedia.Create();
        var uploaded = await media.UploadAndFinalizeAsync(coach, hierarchy.MatchId, SyntheticTimeline.SingleSpan, ct);

        await using var scope = harness.Platform.Services.CreateAsyncScope();
        var lineage = (await scope.ServiceProvider.GetRequiredService<IRecordingSetLookup>().GetAsync(uploaded.RecordingSetVersionId, ct))!;
        var notification = new RecordingsFinalizedNotification(
            Guid.NewGuid(), "1.0.0", DateTimeOffset.UtcNow, lineage.RecordingSetVersionId, lineage.MatchId, lineage.TeamId,
            [.. lineage.Members.Select(m => new FinalizedNotificationMember(
                m.Position, m.RecordingVersionId, m.RecordingContentDigest, m.TimelineMappingId, m.TimelineMappingDigest))]);
        var outcome = await scope.ServiceProvider.GetRequiredService<HandleRecordingsFinalizedNotificationHandler>().HandleAsync(notification, ct);
        return outcome.ShouldBeOfType<NotificationOutcome.RunCreated>().RunId;
    }

    private static async Task WaitForAcceptedAsync(EndToEndHarness harness, Guid runId, CancellationToken ct)
    {
        try
        {
            await EndToEndHarness.WaitAsync(
                async () => await SegmentResultsAsync(harness, runId, ct) >= Segments,
                TimeSpan.FromMinutes(3), "The accepted segment results did not arrive in time.", ct);
        }
        catch (TimeoutException e)
        {
            throw new TimeoutException(e.Message + "\n" + await WorkExecutionEndToEndTests.DiagnosticsAsync(harness, runId, ct));
        }

        (await SegmentResultsAsync(harness, runId, ct)).ShouldBe(Segments);
    }

    private static async Task<long> SegmentResultsAsync(EndToEndHarness harness, Guid runId, CancellationToken ct)
    {
        await using var sql = new NpgsqlConnection(harness.Database.MigratorConnectionString);
        await sql.OpenAsync(ct);
        await using var command = new NpgsqlCommand(
            "SELECT count(DISTINCT j.id) FROM socalytics.analysis_accepted_results r JOIN socalytics.analysis_logical_jobs j ON j.id = r.logical_job_id " +
            "JOIN socalytics.analysis_execution_attempts a ON a.id = r.attempt_id WHERE r.run_id = @runId AND j.segment_number IS NOT NULL", sql);
        command.Parameters.AddWithValue("runId", runId);
        return (long)(await command.ExecuteScalarAsync(ct))!;
    }

    private static async Task<List<Guid>> AttemptIdsAsync(EndToEndHarness harness, Guid runId, CancellationToken ct)
    {
        await using var sql = new NpgsqlConnection(harness.Database.MigratorConnectionString);
        await sql.OpenAsync(ct);
        await using var command = new NpgsqlCommand(
            "SELECT a.id FROM socalytics.analysis_execution_attempts a JOIN socalytics.analysis_logical_jobs j ON j.id = a.logical_job_id WHERE j.run_id = @runId", sql);
        command.Parameters.AddWithValue("runId", runId);
        var ids = new List<Guid>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            ids.Add(reader.GetGuid(0));
        }

        return ids;
    }
}
