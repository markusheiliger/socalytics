namespace SocAlytics.Platform.Domain.Recordings;

public sealed class RecordingDescriptor
{
    public const int MaxDisplayNameLength = 200;
    public const int MaxDescriptionLength = 2000;

    private RecordingDescriptor(string displayName, string? description, string contentType)
    {
        DisplayName = displayName;
        Description = description;
        ContentType = contentType;
    }

    public string DisplayName { get; }

    public string? Description { get; }

    public string ContentType { get; }

    public static bool TryCreate(
        string? displayName,
        string? description,
        string? contentType,
        out RecordingDescriptor? descriptor,
        out IReadOnlyList<FieldViolation> violations)
    {
        var found = new List<FieldViolation>();
        var name = displayName?.Trim() ?? string.Empty;
        if (name.Length is < 1 or > MaxDisplayNameLength)
        {
            found.Add(new FieldViolation("displayName", $"Must be 1 to {MaxDisplayNameLength} characters after trimming."));
        }

        if (description is not null && description.Length > MaxDescriptionLength)
        {
            found.Add(new FieldViolation("description", $"Must be at most {MaxDescriptionLength} characters."));
        }

        var type = contentType?.Trim() ?? string.Empty;
        if (type.Length == 0)
        {
            found.Add(new FieldViolation("contentType", "Must not be empty."));
        }

        violations = found;
        descriptor = found.Count == 0 ? new RecordingDescriptor(name, description, type) : null;
        return found.Count == 0;
    }
}
