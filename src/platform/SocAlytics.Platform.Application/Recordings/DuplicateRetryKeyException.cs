namespace SocAlytics.Platform.Application.Recordings;

/// <summary>The retry outcome primary key already exists; the handler rolls back and replays.</summary>
public sealed class DuplicateRetryKeyException : Exception
{
    public DuplicateRetryKeyException()
        : base("A retry outcome already exists for this operation, match, and idempotency key.")
    {
    }

    public DuplicateRetryKeyException(Exception innerException)
        : base("A retry outcome already exists for this operation, match, and idempotency key.", innerException)
    {
    }
}
