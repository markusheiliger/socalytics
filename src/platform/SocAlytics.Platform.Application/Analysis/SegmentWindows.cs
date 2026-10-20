using System.Text.Json;

namespace SocAlytics.Platform.Application.Analysis;

// The one definition of segment identity and of the half-open nominal window "(n - 1)*D .. n*D" that job
// publication, result acceptance, and the upstream read all use.
public static class SegmentWindows
{
    public static string SegmentId(Guid? recordingVersionId, int segmentNumber) =>
        $"{recordingVersionId}-{segmentNumber}";

    public static long DurationMs(JsonElement runSnapshot) =>
        runSnapshot.GetProperty("segmentationPolicy").GetProperty("durationSeconds").GetInt32() * 1000L;

    public static long DurationMs(string runSnapshotJson)
    {
        using var snapshot = JsonDocument.Parse(runSnapshotJson);
        return DurationMs(snapshot.RootElement);
    }

    public static (long StartMs, long EndMs) Window(int segmentNumber, long durationMs) =>
        ((segmentNumber - 1) * durationMs, segmentNumber * durationMs);
}
