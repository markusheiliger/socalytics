namespace SocAlytics.Platform.Application.Abstractions.ObjectStorage;

/// <summary>A bounded read: <see cref="Content"/> is null exactly when the object is larger than the limit.</summary>
public sealed record ObjectReadResult(byte[]? Content)
{
    public bool TooLarge => Content is null;
}
