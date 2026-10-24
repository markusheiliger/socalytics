using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Shouldly;
using SocAlytics.Platform.Integration.Tests.Infrastructure;
using SocAlytics.Platform.Integration.Tests.Recordings;
using SocAlytics.Platform.Integration.Tests.Recordings.Support;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.Segments;

// FR-024 to FR-027 over HTTP: Club Admin only, anti-forgery on purge, no object keys in responses.
public sealed class MaterializedSegmentEndpointTests(PostgresContainerFixture postgres, RustFsContainerFixture rustfs)
{
    private const string MediaKey = "segments/endpoint-tests/secret-media.mp4";
    private const string ManifestKey = "segments/endpoint-tests/secret-manifest.json";

    private static string Digest(char c) => "sha-256:" + new string(c, 64);

    private static async Task SeedSegmentAsync(FinalizationSetup s, Guid recordingVersionId, int number, CancellationToken ct) =>
        await s.ScalarAsync(
            $"""
            WITH inserted AS (
                INSERT INTO socalytics.materialized_segments
                    (id, recording_version_id, timeline_mapping_digest, segmentation_policy_digest, segment_number, policy_version,
                     duration_seconds, state, demanded, media_object_key, manifest_object_key, media_digest, media_size_bytes,
                     manifest_checksum, created_at, updated_at, available_at)
                VALUES (gen_random_uuid(), '{recordingVersionId}', '{Digest('a')}', '{Digest('b')}', {number}, 1,
                        60, 'available', false, '{MediaKey}{number}', '{ManifestKey}{number}', '{Digest('c')}', 4096,
                        '{Digest('d')}', now(), now(), now())
                RETURNING 1)
            SELECT count(*) FROM inserted
            """,
            ct);

    [Fact]
    public async Task ClubAdminListsAndPurgesWithoutObjectKeysAndRepeatPurgesNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var s = await FinalizationSetup.StartAsync(postgres, rustfs, ct);
        var set = await RecordingFinalizationHelper.FinalizeAsync(s.Admin, s.Hierarchy.MatchId, 1, ct);
        await SeedSegmentAsync(s, set.RecordingVersionIds[0], 1, ct);
        var url = $"/api/v1/matches/{s.Hierarchy.MatchId}/materialized-segments";

        using var list = await s.Admin.SendAsync(HttpMethod.Get, url, null, ct);
        var listText = await list.Content.ReadAsStringAsync(ct);
        list.StatusCode.ShouldBe(HttpStatusCode.OK, listText);
        listText.ShouldNotContain("secret-");
        listText.ShouldNotContain(Digest('c'));
        var body = JsonDocument.Parse(listText).RootElement;
        body.GetProperty("matchId").GetGuid().ShouldBe(s.Hierarchy.MatchId);
        var segment = body.GetProperty("segments").EnumerateArray().Single();
        segment.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ShouldBe(
            ["availableAt", "failureReason", "inUse", "mediaSizeBytes", "recordingVersionId", "segmentNumber",
             "segmentationPolicyDigest", "state", "timelineMappingDigest"],
            ignoreOrder: false);
        segment.GetProperty("state").GetString().ShouldBe("available");
        segment.GetProperty("mediaSizeBytes").GetInt64().ShouldBe(4096);
        segment.GetProperty("inUse").GetBoolean().ShouldBeFalse();
        segment.GetProperty("timelineMappingDigest").GetString().ShouldBe(Digest('a'));

        using var purge = await s.Admin.SendAsync(HttpMethod.Post, url + "/purge", null, ct);
        var purged = await purge.Content.ReadFromJsonAsync<JsonElement>(ct);
        purge.StatusCode.ShouldBe(HttpStatusCode.OK, purged.ToString());
        purged.GetProperty("matchId").GetGuid().ShouldBe(s.Hierarchy.MatchId);
        purged.GetProperty("purged").GetInt32().ShouldBe(1);
        purged.GetProperty("inUse").GetInt32().ShouldBe(0);
        (await s.ScalarAsync("SELECT count(*) FROM socalytics.materialized_segments WHERE state = 'absent'", ct)).ShouldBe(1);

        using var repeat = await s.Admin.SendAsync(HttpMethod.Post, url + "/purge", null, ct);
        (await repeat.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("purged").GetInt32().ShouldBe(0);
    }

    [Fact]
    public async Task PurgeWithoutAntiforgeryTokenIsRefusedAndPurgesNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var s = await FinalizationSetup.StartAsync(postgres, rustfs, ct);
        var set = await RecordingFinalizationHelper.FinalizeAsync(s.Admin, s.Hierarchy.MatchId, 1, ct);
        await SeedSegmentAsync(s, set.RecordingVersionIds[0], 1, ct);

        using var response = await s.Admin.Client.PostAsync($"/api/v1/matches/{s.Hierarchy.MatchId}/materialized-segments/purge", null, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await response.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("code").GetString().ShouldBe("antiforgery-failed");
        (await s.ScalarAsync("SELECT count(*) FROM socalytics.materialized_segments WHERE state = 'available'", ct)).ShouldBe(1);
    }

    [Fact]
    public async Task RefusalsAreUnauthenticatedForbiddenOrNotFoundWithoutDisclosure()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var s = await FinalizationSetup.StartAsync(postgres, rustfs, ct);
        var set = await RecordingFinalizationHelper.FinalizeAsync(s.Admin, s.Hierarchy.MatchId, 1, ct);
        await SeedSegmentAsync(s, set.RecordingVersionIds[0], 1, ct);
        var url = $"/api/v1/matches/{s.Hierarchy.MatchId}/materialized-segments";

        using var anonymous = s.Factory.CreateApiClient();
        using var unauthenticated = await anonymous.GetAsync(url, ct);
        unauthenticated.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        using var coachList = await s.Coach.SendAsync(HttpMethod.Get, url, null, ct);
        coachList.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await coachList.Content.ReadAsStringAsync(ct)).ShouldNotContain("segmentNumber");
        using var coachPurge = await s.Coach.SendAsync(HttpMethod.Post, url + "/purge", null, ct);
        coachPurge.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        var unknown = Guid.NewGuid();
        using var missingList = await s.Admin.SendAsync(HttpMethod.Get, $"/api/v1/matches/{unknown}/materialized-segments", null, ct);
        missingList.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        using var missingPurge = await s.Admin.SendAsync(HttpMethod.Post, $"/api/v1/matches/{unknown}/materialized-segments/purge", null, ct);
        missingPurge.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        (await s.ScalarAsync("SELECT count(*) FROM socalytics.materialized_segments WHERE state = 'available'", ct)).ShouldBe(1);
    }
}
