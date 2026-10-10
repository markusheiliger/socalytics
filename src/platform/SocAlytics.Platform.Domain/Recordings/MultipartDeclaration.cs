namespace SocAlytics.Platform.Domain.Recordings;

public sealed record MultipartBounds(
    long MaxObjectSizeBytes,
    long MinPartSizeBytes,
    long MaxPartSizeBytes,
    int MaxPartCount);

public sealed class MultipartDeclaration
{
    private MultipartDeclaration(
        long totalSizeBytes,
        long partSizeBytes,
        int partCount,
        IReadOnlyList<Sha256Digest> partDigests)
    {
        TotalSizeBytes = totalSizeBytes;
        PartSizeBytes = partSizeBytes;
        PartCount = partCount;
        PartDigests = partDigests;
        ExpectedDigest = CompositeContentDigest.FromPartDigests(partSizeBytes, partDigests);
    }

    public long TotalSizeBytes { get; }

    public long PartSizeBytes { get; }

    public int PartCount { get; }

    public IReadOnlyList<Sha256Digest> PartDigests { get; }

    public CompositeContentDigest ExpectedDigest { get; }

    public long PartSize(int partNumber)
    {
        if (partNumber < 1 || partNumber > PartCount)
        {
            throw new ArgumentOutOfRangeException(nameof(partNumber));
        }

        return partNumber < PartCount ? PartSizeBytes : TotalSizeBytes - (PartCount - 1L) * PartSizeBytes;
    }

    public static bool TryCreate(
        long totalSizeBytes,
        long partSizeBytes,
        IReadOnlyList<string>? partDigests,
        MultipartBounds bounds,
        out MultipartDeclaration? declaration,
        out IReadOnlyList<FieldViolation> violations)
    {
        ArgumentNullException.ThrowIfNull(bounds);
        var found = new List<FieldViolation>();
        declaration = null;

        if (totalSizeBytes < 1 || totalSizeBytes > bounds.MaxObjectSizeBytes)
        {
            found.Add(new FieldViolation("totalSizeBytes", $"Must be between 1 and {bounds.MaxObjectSizeBytes}."));
        }

        if (partSizeBytes < 1 || partSizeBytes > bounds.MaxPartSizeBytes)
        {
            found.Add(new FieldViolation("partSizeBytes", $"Must be between 1 and {bounds.MaxPartSizeBytes}."));
        }

        var count = 0L;
        if (found.Count == 0)
        {
            count = (totalSizeBytes - 1) / partSizeBytes + 1;
            if (count > bounds.MaxPartCount)
            {
                found.Add(new FieldViolation("partSizeBytes", $"Yields {count} parts; at most {bounds.MaxPartCount} are allowed."));
            }
            else if (count > 1 && partSizeBytes < bounds.MinPartSizeBytes)
            {
                found.Add(new FieldViolation("partSizeBytes", $"Must be at least {bounds.MinPartSizeBytes} when there is more than one part."));
            }
        }

        var parsed = new List<Sha256Digest>();
        if (partDigests is null)
        {
            found.Add(new FieldViolation("partDigests", "Is required."));
        }
        else
        {
            for (var i = 0; i < partDigests.Count; i++)
            {
                if (Sha256Digest.TryParse(partDigests[i], out var digest))
                {
                    parsed.Add(digest);
                }
                else
                {
                    found.Add(new FieldViolation($"partDigests[{i}]", "Must be 'sha-256:' followed by 64 lowercase hex characters."));
                }
            }

            if (count > 0 && partDigests.Count != count)
            {
                found.Add(new FieldViolation("partDigests", $"Expected {count} digests but received {partDigests.Count}."));
            }
        }

        violations = found;
        if (found.Count > 0)
        {
            return false;
        }

        declaration = new MultipartDeclaration(totalSizeBytes, partSizeBytes, (int)count, parsed);
        return true;
    }
}
