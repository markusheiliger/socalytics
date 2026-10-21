using SocAlytics.Platform.Domain.Segments;

namespace SocAlytics.Platform.Application.Segments;

// Disposing deletes the private working directory that holds the media file.
public sealed class ExtractedSegment : IAsyncDisposable
{
    private readonly string workingDirectory;

    public ExtractedSegment(string workingDirectory, string mediaFilePath, long sizeBytes, SegmentInterval encodedInterval, string toolchain)
    {
        this.workingDirectory = workingDirectory;
        MediaFilePath = mediaFilePath;
        SizeBytes = sizeBytes;
        EncodedInterval = encodedInterval;
        Toolchain = toolchain;
    }

    public string MediaFilePath { get; }

    public long SizeBytes { get; }

    // First and last video packet boundaries in source media milliseconds.
    public SegmentInterval EncodedInterval { get; }

    public string Toolchain { get; }

    public ValueTask DisposeAsync()
    {
        try
        {
            Directory.Delete(workingDirectory, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return ValueTask.CompletedTask;
    }
}

// Carries a sanitized reason from SegmentFailureReasons; the message never contains source URLs.
public sealed class SegmentExtractionException : Exception
{
    public SegmentExtractionException(string reason, bool transient, Exception? innerException = null)
        : base($"Segment extraction failed: {reason}.", innerException)
    {
        Reason = reason;
        Transient = transient;
    }

    public string Reason { get; }

    public bool Transient { get; }
}

public interface ISegmentExtractor
{
    // Stream-copies the requested source interval from the presigned read URL into a private working directory.
    Task<ExtractedSegment> ExtractAsync(Uri sourceReadUrl, SegmentInterval requestedSourceInterval, CancellationToken cancellationToken);
}
