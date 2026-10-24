using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Shouldly;
using SocAlytics.Platform.EndToEnd.Tests.Support;
using SocAlytics.Platform.Integration.Tests;
using SocAlytics.Platform.Integration.Tests.IdentityAccess.Support;
using Xunit;

namespace SocAlytics.Platform.EndToEnd.Tests;

/// <summary>
/// After the development workflow completes over an uploaded synthetic recording, a Coach resolves an indexed fact
/// to evidence media, waits for preparing parts, and downloads media whose digest equals the returned digest (FR-024).
/// </summary>
[Collection("EndToEnd")]
public sealed class EvidenceMediaEndToEndTests
{
    private const int PolicySeconds = 5;

    [Fact]
    public async Task A_coach_resolves_an_indexed_fact_and_downloads_evidence_media_matching_its_digest()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(6));
        var ct = cts.Token;

        var settings = new Dictionary<string, string?>
        {
            ["Segments:Toolchain:FfmpegPath"] = SyntheticMedia.FfmpegPath,
            ["Segments:Toolchain:FfprobePath"] = Path.Combine(Path.GetDirectoryName(SyntheticMedia.FfmpegPath) ?? string.Empty, "ffprobe"),
            ["Analysis:DefaultWorkflow:Nodes:0:VideoPolicy"] = "required",
            ["Analysis:DefaultWorkflow:SegmentationPolicy:DurationSeconds"] = PolicySeconds.ToString(),
        };
        var started = await EndToEndHarness.StartAsync(ct, additionalSettings: settings);
        if (started is null)
        {
            Assert.Skip("SoftHSM2 (libsofthsm2 and softhsm2-util) is required on Linux for the Manager's device key.");
            return;
        }

        await using var harness = started;
        await harness.StartManagerAsync(ct, TimeSpan.FromSeconds(1));

        var runId = await SegmentMediaEndToEndTests.FinalizeAndStartRunAsync(harness, ct);
        await SegmentMediaEndToEndTests.WaitForAcceptedAsync(harness, runId, ct);

        using var coach = await ApiSession.SignInAsync(harness.Platform, "coach-e2e", TestMembers.DefaultPassword, ct);
        var (acceptedResultId, ordinal) = await FirstSegmentFactAsync(coach, runId, ct);

        JsonElement[] parts = [];
        await EndToEndHarness.WaitAsync(
            async () =>
            {
                using var response = await coach.SendAsync(
                    HttpMethod.Post, $"/api/v1/analysis/runs/{runId}/evidence-media",
                    new { fact = new { acceptedResultId, ordinal, contextBeforeMs = 0, contextAfterMs = 0 } }, ct);
                var text = await response.Content.ReadAsStringAsync(ct);
                response.StatusCode.ShouldBe(HttpStatusCode.OK, text);
                parts = [.. JsonDocument.Parse(text).RootElement.GetProperty("parts").EnumerateArray()];
                parts.ShouldNotBeEmpty();
                return !parts.Any(p => p.GetProperty("state").GetString() is "materializing" or "rematerializing");
            },
            TimeSpan.FromMinutes(2), "The evidence parts did not leave the preparing states in time.", ct);

        var available = parts.Where(p => p.GetProperty("state").GetString() == "available").ToList();
        available.ShouldNotBeEmpty(string.Join(", ", parts.Select(p => p.GetProperty("state").GetString())));

        using var download = new HttpClient();
        foreach (var part in available)
        {
            var media = part.GetProperty("media");
            media.GetProperty("mediaType").GetString().ShouldBe("video/mp4");
            using var response = await download.GetAsync(media.GetProperty("url").GetString(), ct);
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            var bytes = await response.Content.ReadAsByteArrayAsync(ct);
            bytes.LongLength.ShouldBe(media.GetProperty("mediaSizeBytes").GetInt64());
            ("sha-256:" + Convert.ToHexStringLower(SHA256.HashData(bytes))).ShouldBe(media.GetProperty("mediaDigest").GetString());
        }
    }

    private static async Task<(Guid AcceptedResultId, int Ordinal)> FirstSegmentFactAsync(ApiSession coach, Guid runId, CancellationToken ct)
    {
        using var streamsResponse = await coach.Client.GetAsync($"/api/v1/analysis/runs/{runId}/fact-streams", ct);
        streamsResponse.StatusCode.ShouldBe(HttpStatusCode.OK, await streamsResponse.Content.ReadAsStringAsync(ct));
        var streams = (await streamsResponse.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("items").EnumerateArray().ToList();
        var stream = streams.First(s => s.GetProperty("executionScope").GetString() == "segment");
        var capability = stream.GetProperty("capability");

        var path = $"/api/v1/analysis/runs/{runId}/facts?capabilityId={capability.GetProperty("id").GetString()}" +
            $"&capabilityVersion={capability.GetProperty("version").GetString()}&schemaVersion={stream.GetProperty("schemaVersion").GetString()}" +
            $"&nodeKey={stream.GetProperty("nodeKey").GetString()}&pageSize=1";
        using var response = await coach.Client.GetAsync(path, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, text);
        var item = JsonDocument.Parse(text).RootElement.GetProperty("items").EnumerateArray().First();
        return (item.GetProperty("acceptedResultId").GetGuid(), item.GetProperty("ordinal").GetInt32());
    }
}
